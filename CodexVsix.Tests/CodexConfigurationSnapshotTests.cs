using System;
using System.IO;
using System.Text;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexConfigurationSnapshotTests
{
    [Fact]
    public void CopyPreservesUtf8BomCrLfCommentsAndRebasesDocumentedRelativeReferences()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "shared", "config.toml");
        var target = Path.Combine(directory.Path, "private", "config.toml");
        var contents = "# keep this comment\r\n"
            + "model_catalog_json = \"./models\\u002Dcatalog.json\" # keep catalog comment\r\n"
            + "model_instructions_file = 'instructions.md'\r\n"
            + "experimental_instructions_file = \"legacy\\\\instructions.md\"\r\n"
            + "agents.writer.config_file = \"agents/\\U0001F4A1.toml\"\r\n"
            + "[agents.reviewer]\r\n"
            + "config_file = 'agents/审查.toml' # keep agent comment\r\n"
            + "[profiles.fast]\r\n"
            + "model_catalog_json = \"profile-models.json\"\r\n"
            + "model_instructions_file = \"profile-instructions.md\"\r\n"
            + "[model_providers.example]\r\n"
            + "config_file = \"must-stay-relative.toml\"\r\n";
        WriteUtf8(source, contents, includeBom: true);

        CodexConfigurationSnapshot.Copy(source, target);

        var snapshot = File.ReadAllText(target, new UTF8Encoding(false, true));
        Assert.Contains("# keep this comment\r\n", snapshot);
        Assert.Contains("# keep catalog comment\r\n", snapshot);
        Assert.Contains("# keep agent comment\r\n", snapshot);
        Assert.Contains("config_file = \"must-stay-relative.toml\"", snapshot);
        Assert.Contains("model_catalog_json = " + EncodedPath(source, "models-catalog.json"), snapshot);
        Assert.Contains("model_instructions_file = " + EncodedPath(source, "instructions.md"), snapshot);
        Assert.Contains("experimental_instructions_file = " + EncodedPath(source, "legacy\\instructions.md"), snapshot);
        Assert.Contains("config_file = " + EncodedPath(source, "agents\\审查.toml"), snapshot);
        Assert.Contains("agents.writer.config_file = " + EncodedPath(source, "agents\\💡.toml"), snapshot);
        Assert.Contains("model_catalog_json = " + EncodedPath(source, "profile-models.json"), snapshot);
        Assert.Contains("model_instructions_file = " + EncodedPath(source, "profile-instructions.md"), snapshot);
        Assert.True(ReadBytes(target)[0] == 0xef && ReadBytes(target)[1] == 0xbb && ReadBytes(target)[2] == 0xbf);
        Assert.DoesNotMatch("(?<!\\r)\\n", snapshot);
    }

    [Fact]
    public void CopyLeavesAbsoluteReferencesAndUnrelatedTextUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.toml");
        var target = Path.Combine(directory.Path, "snapshot.toml");
        var absolute = Path.Combine(directory.Path, "external", "model.json");
        var contents = "model_catalog_json = " + CodexAppServerCommandLine.EncodeTomlString(absolute) + "\n"
            + "model_provider = \"openai\"\n"
            + "[agents.worker]\n"
            + "config_file = " + CodexAppServerCommandLine.EncodeTomlString(absolute) + "\n";
        WriteUtf8(source, contents, includeBom: false);

        CodexConfigurationSnapshot.Copy(source, target);

        Assert.Equal(contents, File.ReadAllText(target, new UTF8Encoding(false, true)));
    }

    [Theory]
    [InlineData("model_catalog_json")]
    [InlineData("model_instructions_file")]
    [InlineData("experimental_instructions_file")]
    public void CopyRejectsUnsupportedMultilineDocumentedReferences(string key)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.toml");
        var target = Path.Combine(directory.Path, "target.toml");
        WriteUtf8(source, key + " = \"\"\"\nrelative-file\n\"\"\"\n", includeBom: false);

        var exception = Assert.Throws<InvalidDataException>(() => CodexConfigurationSnapshot.Copy(source, target));

        Assert.Contains(key, exception.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void CopyRejectsMultilineAgentConfigFile()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.toml");
        var target = Path.Combine(directory.Path, "target.toml");
        WriteUtf8(source, "[agents.reviewer]\nconfig_file = '''\nagent.toml\n'''\n", includeBom: false);

        Assert.Throws<InvalidDataException>(() => CodexConfigurationSnapshot.Copy(source, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void CopySkipsFauxSectionsAndReferencesInsideUnrelatedMultilineText()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.toml");
        var target = Path.Combine(directory.Path, "target.toml");
        var contents = "developer_instructions = \"\"\"\n[agents.faux]\nconfig_file = \"do-not-rebase.toml\"\nmodel_catalog_json = \"do-not-rebase.json\"\n\"\"\"\n"
            + "model_catalog_json = \"real.json\"\n";
        WriteUtf8(source, contents, includeBom: false);

        CodexConfigurationSnapshot.Copy(source, target);

        var snapshot = File.ReadAllText(target, new UTF8Encoding(false, true));
        Assert.Contains("config_file = \"do-not-rebase.toml\"", snapshot);
        Assert.Contains("model_catalog_json = \"do-not-rebase.json\"", snapshot);
        Assert.Contains("model_catalog_json = " + EncodedPath(source, "real.json"), snapshot);
    }

    [Fact]
    public void CopyDoesNotOverwriteAnExistingSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.toml");
        var target = Path.Combine(directory.Path, "target.toml");
        WriteUtf8(source, "model_catalog_json = \"relative.json\"\n", includeBom: false);
        WriteUtf8(target, "user edit\n", includeBom: false);

        Assert.Throws<IOException>(() => CodexConfigurationSnapshot.Copy(source, target));
        Assert.Equal("user edit\n", File.ReadAllText(target, new UTF8Encoding(false, true)));
    }

    private static string EncodedPath(string sourceFile, string relativePath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFile)!;
        return CodexAppServerCommandLine.EncodeTomlString(Path.GetFullPath(Path.Combine(sourceDirectory, relativePath)));
    }

    private static void WriteUtf8(string path, string contents, bool includeBom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(includeBom));
    }

    private static byte[] ReadBytes(string path) => File.ReadAllBytes(path);
}
