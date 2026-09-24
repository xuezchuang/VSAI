using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexVsix.Services;

/// <summary>Copies a user configuration while retaining documented read-only assets in their shared location.</summary>
internal static class CodexConfigurationSnapshot
{
    internal static void Copy(string sourceFile, string targetFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile)) throw new ArgumentException("A source configuration file is required.", nameof(sourceFile));
        if (string.IsNullOrWhiteSpace(targetFile)) throw new ArgumentException("A target configuration file is required.", nameof(targetFile));

        var sourcePath = Path.GetFullPath(sourceFile);
        var targetPath = Path.GetFullPath(targetFile);
        var bytes = File.ReadAllBytes(sourcePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The source configuration must be valid UTF-8.", exception);
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidDataException("The source configuration has no directory.");
        var snapshot = RebaseReadOnlyReferences(text, sourceDirectory);
        WriteAtomically(targetPath, snapshot, hasBom);
    }

    private static string RebaseReadOnlyReferences(string text, string sourceDirectory)
    {
        var result = new StringBuilder(text.Length);
        var section = Array.Empty<string>();
        string? multilineDelimiter = null;
        for (var offset = 0; offset < text.Length;)
        {
            var lineEnd = offset;
            while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n') lineEnd++;
            var line = text.Substring(offset, lineEnd - offset);
            var newlineLength = lineEnd < text.Length && text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n'
                ? 2 : lineEnd < text.Length ? 1 : 0;

            if (multilineDelimiter is not null)
            {
                if (FindMultilineEnd(line, multilineDelimiter, 0) >= 0)
                    multilineDelimiter = null;
            }
            else if (TryReadSection(line, out var nextSection))
            {
                section = nextSection;
            }
            else if (TryReadAssignment(line, out var keyParts, out var valueStart))
            {
                var isReadOnlyReference = IsReadOnlyReference(section, keyParts);
                if (StartsMultilineString(line, valueStart, out var delimiter))
                {
                    if (isReadOnlyReference)
                        throw new InvalidDataException("The " + keyParts[keyParts.Length - 1]
                            + " configuration value must be a single-line TOML string.");
                    if (FindMultilineEnd(line, delimiter, valueStart + delimiter.Length) < 0)
                        multilineDelimiter = delimiter;
                }
                else if (isReadOnlyReference)
                {
                    if (!TryReadTomlString(line, valueStart, out var valueEnd, out var value))
                        throw new InvalidDataException("The " + keyParts[keyParts.Length - 1]
                            + " configuration value must be a single-line TOML string.");

                    if (!Path.IsPathRooted(value.Decoded))
                    {
                        if (string.IsNullOrWhiteSpace(value.Decoded))
                            throw new InvalidDataException("A relative configuration reference must not be empty.");
                        string absolute;
                        try { absolute = Path.GetFullPath(Path.Combine(sourceDirectory, value.Decoded)); }
                        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
                        {
                            throw new InvalidDataException("The configuration reference is not a valid relative path.", exception);
                        }

                        line = line.Substring(0, valueStart)
                            + CodexAppServerCommandLine.EncodeTomlString(absolute)
                            + line.Substring(valueEnd);
                    }
                }
            }

            result.Append(line);
            if (newlineLength > 0) result.Append(text, lineEnd, newlineLength);
            offset = lineEnd + newlineLength;
        }

        return result.ToString();
    }

    private static bool IsReadOnlyReference(string[] section, string[] keyParts)
    {
        var allParts = new string[section.Length + keyParts.Length];
        Array.Copy(section, allParts, section.Length);
        Array.Copy(keyParts, 0, allParts, section.Length, keyParts.Length);
        var key = allParts[allParts.Length - 1];
        if (string.Equals(key, "model_catalog_json", StringComparison.Ordinal)
            || string.Equals(key, "model_instructions_file", StringComparison.Ordinal)
            || string.Equals(key, "experimental_instructions_file", StringComparison.Ordinal))
        {
            return allParts.Length == 1
                || allParts.Length == 3 && string.Equals(allParts[0], "profiles", StringComparison.Ordinal);
        }

        return allParts.Length >= 2 && string.Equals(allParts[0], "agents", StringComparison.Ordinal)
            && string.Equals(key, "config_file", StringComparison.Ordinal);
    }

    private static bool TryReadSection(string line, out string[] section)
    {
        section = Array.Empty<string>();
        var trimmed = StripComment(line).Trim();
        if (trimmed.Length < 3 || trimmed[0] != '[') return false;
        var isArray = trimmed.StartsWith("[[", StringComparison.Ordinal);
        var close = isArray ? "]]" : "]";
        if (!trimmed.EndsWith(close, StringComparison.Ordinal)) return false;
        var start = isArray ? 2 : 1;
        var name = trimmed.Substring(start, trimmed.Length - start - close.Length).Trim();
        return TryReadDottedName(name, out section);
    }

    private static bool TryReadAssignment(string line, out string[] keyParts, out int valueStart)
    {
        keyParts = Array.Empty<string>();
        valueStart = 0;
        var equals = FindAssignment(line);
        if (equals < 0 || !TryReadDottedName(line.Substring(0, equals).Trim(), out keyParts)) return false;
        valueStart = equals + 1;
        while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart])) valueStart++;
        return valueStart < line.Length;
    }

    private static bool TryReadTomlString(string line, int valueStart, out int valueEnd, out TomlString value)
    {
        valueEnd = 0;
        value = TomlString.Empty;
        var quote = line[valueStart];
        if (quote != '\'' && quote != '"') return false;
        var cursor = valueStart + 1;
        var decoded = new StringBuilder();
        while (cursor < line.Length)
        {
            var character = line[cursor++];
            if (character == quote)
            {
                valueEnd = cursor;
                while (cursor < line.Length && char.IsWhiteSpace(line[cursor])) cursor++;
                if (cursor < line.Length && line[cursor] != '#') return false;
                value = new TomlString(decoded.ToString());
                return true;
            }

            if (quote == '"' && character == '\\')
            {
                if (cursor >= line.Length) return false;
                decoded.Append(DecodeBasicEscape(line, ref cursor));
            }
            else decoded.Append(character);
        }

        return false;
    }

    private static string DecodeBasicEscape(string value, ref int cursor)
    {
        var escape = value[cursor++];
        switch (escape)
        {
            case 'b': return "\b";
            case 't': return "\t";
            case 'n': return "\n";
            case 'f': return "\f";
            case 'r': return "\r";
            case '"': return "\"";
            case '\\': return "\\";
            case '/': return "/";
            case 'u': return DecodeUnicodeEscape(value, ref cursor, 4);
            case 'U':
                var scalar = DecodeHex(value, ref cursor, 8);
                if (scalar > 0x10ffff || (scalar >= 0xd800 && scalar <= 0xdfff))
                    throw new InvalidDataException("The TOML Unicode escape is not a valid scalar value.");
                return char.ConvertFromUtf32(scalar);
            default: throw new InvalidDataException("The TOML path contains an unsupported basic-string escape.");
        }
    }

    private static string DecodeUnicodeEscape(string value, ref int cursor, int count)
    {
        var scalar = DecodeHex(value, ref cursor, count);
        if (scalar >= 0xd800 && scalar <= 0xdfff)
            throw new InvalidDataException("The TOML Unicode escape is not a valid scalar value.");
        return char.ConvertFromUtf32(scalar);
    }

    private static int DecodeHex(string value, ref int cursor, int count)
    {
        if (cursor + count > value.Length) throw new InvalidDataException("The TOML Unicode escape is incomplete.");
        long scalar = 0;
        for (var index = 0; index < count; index++)
        {
            var digit = value[cursor++];
            if (digit >= '0' && digit <= '9') scalar = scalar * 16 + digit - '0';
            else if (digit >= 'a' && digit <= 'f') scalar = scalar * 16 + digit - 'a' + 10;
            else if (digit >= 'A' && digit <= 'F') scalar = scalar * 16 + digit - 'A' + 10;
            else throw new InvalidDataException("The TOML Unicode escape contains a non-hexadecimal character.");
        }

        if (scalar > 0x10ffff) throw new InvalidDataException("The TOML Unicode escape is not a valid scalar value.");
        return (int)scalar;
    }

    private static bool StartsMultilineString(string line, int start, out string delimiter)
    {
        delimiter = string.Empty;
        if (start + 2 >= line.Length) return false;
        if (line[start] == '"' && line[start + 1] == '"' && line[start + 2] == '"') delimiter = "\"\"\"";
        else if (line[start] == '\'' && line[start + 1] == '\'' && line[start + 2] == '\'') delimiter = "'''";
        return delimiter.Length != 0;
    }

    private static int FindMultilineEnd(string line, string delimiter, int start)
    {
        for (var index = line.IndexOf(delimiter, start, StringComparison.Ordinal); index >= 0;
            index = line.IndexOf(delimiter, index + 1, StringComparison.Ordinal))
        {
            var escapes = 0;
            if (delimiter[0] == '"')
                for (var previous = index - 1; previous >= 0 && line[previous] == '\\'; previous--) escapes++;
            if (escapes % 2 == 0) return index;
        }
        return -1;
    }

    private static int FindAssignment(string line)
    {
        var inBasic = false;
        var inLiteral = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inBasic)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inBasic = false;
                continue;
            }

            if (inLiteral)
            {
                if (character == '\'') inLiteral = false;
                continue;
            }

            if (character == '#') return -1;
            if (character == '"') { inBasic = true; continue; }
            if (character == '\'') { inLiteral = true; continue; }
            if (character == '=') return index;
        }

        return -1;
    }

    private static string StripComment(string line)
    {
        var assignment = FindComment(line);
        return assignment < 0 ? line : line.Substring(0, assignment);
    }

    private static int FindComment(string line)
    {
        var inBasic = false;
        var inLiteral = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inBasic)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inBasic = false;
                continue;
            }

            if (inLiteral)
            {
                if (character == '\'') inLiteral = false;
                continue;
            }

            if (character == '"') inBasic = true;
            else if (character == '\'') inLiteral = true;
            else if (character == '#') return index;
        }

        return -1;
    }

    private static bool TryReadDottedName(string value, out string[] parts)
    {
        var result = new List<string>();
        var cursor = 0;
        while (cursor < value.Length)
        {
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) cursor++;
            if (cursor >= value.Length) break;
            string part;
            if (value[cursor] == '"' || value[cursor] == '\'')
            {
                var quote = value[cursor++];
                var decoded = new StringBuilder();
                while (cursor < value.Length && value[cursor] != quote)
                {
                    var character = value[cursor++];
                    if (quote == '"' && character == '\\')
                    {
                        if (cursor >= value.Length) { parts = Array.Empty<string>(); return false; }
                        decoded.Append(DecodeBasicEscape(value, ref cursor));
                    }
                    else decoded.Append(character);
                }
                if (cursor >= value.Length) { parts = Array.Empty<string>(); return false; }
                cursor++;
                part = decoded.ToString();
            }
            else
            {
                var start = cursor;
                while (cursor < value.Length && value[cursor] != '.' && !char.IsWhiteSpace(value[cursor])) cursor++;
                part = value.Substring(start, cursor - start);
            }

            if (string.IsNullOrWhiteSpace(part)) { parts = Array.Empty<string>(); return false; }
            result.Add(part);
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) cursor++;
            if (cursor == value.Length) break;
            if (value[cursor++] != '.') { parts = Array.Empty<string>(); return false; }
        }

        parts = result.ToArray();
        return parts.Length > 0;
    }

    private static void WriteAtomically(string targetPath, string text, bool includeBom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(includeBom));
            if (File.Exists(targetPath)) throw new IOException("The configuration snapshot target already exists.");
            File.Move(temporary, targetPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private readonly struct TomlString
    {
        public static TomlString Empty => new(string.Empty);

        public TomlString(string decoded)
        {
            Decoded = decoded;
        }

        public string Decoded { get; }
    }
}
