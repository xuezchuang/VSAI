using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace CodexVsix.Services;

// Keep the upstream bundle immutable. Version-guarded runtime copies adapt effort
// controls and the fork protocol; the model catalog still controls availability.
internal static class CodexReasoningEffortWebViewCompatibility
{
    internal const string ComposerModule = "composer-B3BCMq_W.js";
    internal const string ComposerViewStateModule = "composer-view-state-snaLlb2D.js";
    internal const string LocalConversationThreadModule = "local-conversation-thread-WLjaXjZ7.js";
    internal const string LabelModule = "reasoning-minimal-BmczWw15.js";
    internal const string SettingsModule = "use-model-settings-D_RJ6qrG.js";
    internal const string ModelQueriesModule = "model-queries-BOnPKCmp.js";
    internal const string AssetBaseUrl = "https://" + CodexOfficialWebViewShell.AssetHostName + "/webview/assets/";

    private static readonly IReadOnlyDictionary<string, string> ModuleHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ComposerModule] = "03b05c10f98be300790c094ee70d258907835c2cbcddc5b382d1dc97ed1f6b8d",
            [ComposerViewStateModule] = "e5af6f9162d951544cd80040ff06b41f579d6fcbdec2d51d249a401bb19ecf26",
            [LocalConversationThreadModule] = "15045243b7652d771d8d869885284c136ddc6d2541a0791f938a54afb6dd95f4",
            [LabelModule] = "752677eaa4d9f3021b6ac4a05c6a0f778301ad2d4449f655bb01afa9316e0907",
            [SettingsModule] = "1fc45f6af86f26d85b653b2c835210dc9f999752527e8e824b5fc229045d2c6c",
            [ModelQueriesModule] = "9713d9213242bdcf73c3a15acff4323388d2b6b9922c073616536936dde700e5",
            [CodexConversationForkWebViewCompatibility.ManagerModule] = "2250fa42452e7629d8c4194f9d2d0e849f232ad05d1286886caae960e0ec945d"
        };

    private static readonly Regex RelativeAssetLiteral = new(
        "([\"'`])\\./([A-Za-z0-9_.-]+\\.(?:js|css))\\1",
        RegexOptions.Compiled);

    internal static string CreateImportMap(string resourceRoot, string shellDirectory, string webviewId)
    {
        if (!Regex.IsMatch(webviewId, @"\A[A-Za-z0-9_-]+\z"))
        {
            throw new ArgumentException("The webview ID must be a safe directory name.", nameof(webviewId));
        }

        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in ModuleHashes)
        {
            var source = File.ReadAllText(Path.Combine(resourceRoot, "webview", "assets", entry.Key));
            modules.Add(entry.Key, AdaptModule(entry.Key, source));
        }

        // Each host owns its directory so independent VS tool windows never overwrite
        // a module while another host is loading it. Recovery reuses the same contents.
        var directoryName = "reasoning-compatibility-" + webviewId;
        var outputDirectory = Path.Combine(shellDirectory, directoryName);
        Directory.CreateDirectory(outputDirectory);
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            File.WriteAllText(Path.Combine(outputDirectory, module.Key), module.Value, new UTF8Encoding(false));
            imports.Add(AssetBaseUrl + module.Key,
                "https://" + CodexOfficialWebViewShell.ShellHostName + "/" + directoryName + "/" + module.Key);
        }

        return "<script type=\"importmap\">"
            + JsonConvert.SerializeObject(new { imports })
            + "</script>";
    }

    internal static string AdaptModule(string moduleName, string source)
    {
        // Normalize only line endings for the digest, allowing Git's Windows checkout
        // conversion without accepting a different bundle or a partly patched source.
        using (var sha256 = SHA256.Create())
        {
            var digest = BitConverter.ToString(sha256.ComputeHash(
                Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n")))).Replace("-", string.Empty).ToLowerInvariant();
            if (!ModuleHashes.TryGetValue(moduleName, out var expected)
                || !string.Equals(digest, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The frozen Codex compatibility module has changed: " + moduleName);
            }
        }

        switch (moduleName)
        {
            case ComposerViewStateModule:
                // Store the conversation origin beside the selected text so even
                // duplicate snippets point back to the exact turn and text range.
                source = ReplaceOnce(source,
                    "function Q(e,n,r){let i=n.trim();i.length!==0&&Z(e,e=>{e.selectedTextAttachments.push({id:t(),text:r==null?i:n,...r==null?{}:{source:r}})})}",
                    "function Q(e,n,r,a){let i=n.trim();i.length!==0&&Z(e,e=>{e.selectedTextAttachments.push({id:t(),text:r==null?i:n,...r==null?{}:{source:r},...a==null?{}:{vsaiChatOrigin:a}})})}");
                break;
            case LocalConversationThreadModule:
                // Reuse the official virtualized turn list's scroll API. Direct
                // DOM scrolling cannot reach turns that are currently unmounted.
                source = ReplaceOnce(source,
                    "Ce=(0,X.useMemo)(()=>Yp(",
                    "vsaiScrollEffect=(0,X.useEffect)(()=>{window.__vsaiChatSelectionNavigation?.registerScroll(e,Se.scrollToTurn);return()=>window.__vsaiChatSelectionNavigation?.unregisterScroll(e,Se.scrollToTurn)},[e,Se]),Ce=(0,X.useMemo)(()=>Yp(");
                break;
            case CodexConversationForkWebViewCompatibility.ManagerModule:
                source = CodexConversationForkWebViewCompatibility.AdaptModule(source);
                // History-list placeholders have no known model yet. A hard-coded
                // model here is mistaken for a real previous model on resume.
                source = ReplaceOnce(source,
                    "model:`gpt-5.5`,effort:`medium`,summary:`none`",
                    "model:``,effort:`medium`,summary:`none`");
                source = ReplaceOnce(source,
                    "previousTurnModel:null,latestCollaborationMode:{mode:`default`,settings:{reasoning_effort:`medium`,model:`gpt-5.5`,developer_instructions:null}}",
                    "previousTurnModel:null,latestCollaborationMode:{mode:`default`,settings:{reasoning_effort:`medium`,model:``,developer_instructions:null}}");
                source = ReplaceOnce(source,
                    "hasUnreadTurn:this.params.getThreadHasUnreadTurn(t),latestCollaborationMode:{mode:`default`,settings:{reasoning_effort:`medium`,model:`gpt-5.5`,developer_instructions:null}}",
                    "hasUnreadTurn:this.params.getThreadHasUnreadTurn(t),latestCollaborationMode:{mode:`default`,settings:{reasoning_effort:`medium`,model:``,developer_instructions:null}}");
                // The history list carries the persisted provider. The picker built
                // from a placeholder model can otherwise report the global provider.
                source = ReplaceOnce(source,
                    "path:l?.rolloutPath??null,model:null,modelProvider:N.modelProvider,serviceTier:N.serviceTier",
                    "path:l?.rolloutPath??null,model:null,modelProvider:l?.modelProvider??N.modelProvider,serviceTier:N.serviceTier");
                // Sent selections are reconstructed from the prompt. Preserve the
                // source heading for navigation while keeping plain snippets intact.
                source = ReplaceOnce(source,
                    "function sf(e){return ",
                    "function sf(e){let h=e[0]?.match(/^## Selection \\d+: (.+) \\(lines? (\\d+)(?:-(\\d+))?\\)$/),line=Number(h?.[2]);if(h?.[1]&&Number.isSafeInteger(line)&&line>0)return{text:e.slice(1).join(String.fromCharCode(10)).trim(),source:{path:h[1],range:{start:{line:line-1,character:0},end:{line:line-1,character:0}}}};return ");
                break;
            case ComposerModule:
                // The shared-object hook reacts to prefill updates on every route.
                // In a follow-up, append selections without replacing its draft.
                source = ReplaceOnce(source,
                    "let e=ko?.commentAttachments;ko==null||!ko.text&&(e==null||e.length===0)||(K??(ko.cwd==null?H.set(Vs,null):H.set(Vs,ko.cwd),ko.text&&(ir(ko.text)?X.setPromptText(ko.text):X.setText(ko.text)),e!=null&&e.length>0&&ya(e),X.focus(),Ao(void 0))",
                    "let e=ko?.commentAttachments,t=ko?.selectedTextAttachments;ko==null||!ko.text&&(e==null||e.length===0)&&(t==null||t.length===0)||(K?(t!=null&&t.length>0&&(xa(e=>[...e,...t]),X.focus(),Ao(void 0))):(ko.cwd==null?H.set(Vs,null):H.set(Vs,ko.cwd),ko.text&&(ir(ko.text)?X.setPromptText(ko.text):X.setText(ko.text)),e!=null&&e.length>0&&ya(e),t!=null&&t.length>0&&xa(e=>[...e,...t]),X.focus(),Ao(void 0))");
                source = ReplaceOnce(source,
                    "selections:v.map(Qd),onRemove:E",
                    "selections:v,onRemove:E");
                source = ReplaceOnce(source,
                    "function Ld(e){",
                    "function __vsaiSelectionTarget(e){let s=e?.source,p=s?.path,l=s?.range?.start?.line,c=s?.range?.start?.character;return typeof p===`string`&&p.length>0&&Number.isSafeInteger(l)&&l>=0?{path:p,line:l+1,...Number.isSafeInteger(c)&&c>=0?{column:c+1}:{}}:null}function __vsaiOpenSelection(e){let t=__vsaiSelectionTarget(e);if(t!=null)__vsaiOpenFile(t);else if(e?.vsaiChatOrigin!=null)window.__vsaiChatSelectionNavigation?.reveal(e.vsaiChatOrigin)}function Ld(e){");
                source = ReplaceOnce(source,
                    "l=(0,Q.jsx)(Pd,{Icon:Gc,label:a,onRemove:r,onRemoveAriaLabel:o,popoverClassName:`w-fit gap-2 px-2 py-1`,popoverContent:s,popoverStyle:c})",
                    "l=(0,Q.jsx)(`span`,{onClick:()=>{n.length===1&&__vsaiOpenSelection(n[0])},children:(0,Q.jsx)(Pd,{Icon:Gc,label:a,onRemove:r,onRemoveAriaLabel:o,popoverClassName:`w-fit gap-2 px-2 py-1`,popoverContent:s,popoverStyle:c})})");
                source = ReplaceOnce(source,
                    "function zd(e,t){return(0,Q.jsx)(`span`,{className:`line-clamp-3 break-words`,children:(0,Q.jsx)(Y,{id:`selectedTextAttachments.tooltipSnippet`,defaultMessage:`\"{text}\"`,description:`Selected text snippet shown inside the selected text attachment tooltip`,values:{text:e}})},`${t}-${e}`)}",
                    "function zd(e,t){let n=typeof e===`string`?e:e?.text??``,r=__vsaiSelectionTarget(e),a=e?.vsaiChatOrigin,c=(0,Q.jsx)(Y,{id:`selectedTextAttachments.tooltipSnippet`,defaultMessage:`\"{text}\"`,description:`Selected text snippet shown inside the selected text attachment tooltip`,values:{text:n}});return r==null&&a==null?(0,Q.jsx)(`span`,{className:`line-clamp-3 break-words`,children:c},`${t}-${n}`):(0,Q.jsx)(`button`,{type:`button`,className:`line-clamp-3 cursor-interaction break-words text-left hover:underline`,title:r?`${r.path}:${r.line}`:n,onClick:t=>{t.stopPropagation();__vsaiOpenSelection(e)},children:c},`${t}-${n}`)}");
                source = ReplaceOnce(source,
                    "a=()=>{window.getSelection()?.removeAllRanges(),n(i)}",
                    "a=()=>{let e=window.__vsaiChatSelectionNavigation?.capture(i);window.getSelection()?.removeAllRanges(),n(i,e)}");
                source = ReplaceOnce(source,
                    "_c=(0,Z.useCallback)(e=>{e.trim().length!==0&&(Cr(H,e),Do())},[Do,H])",
                    "_c=(0,Z.useCallback)((e,t)=>{e.trim().length!==0&&(Cr(H,e,void 0,t==null?null:{...t,conversationId:K}),Do())},[Do,H,K])");
                source = "import{t as __vsaiOpenFile}from\"./send-open-file-request-CF2gTAWF.js\";\n" + source;
                // Reuse the existing highest-effort glyph, without changing the value
                // used by the menu, selection callback, telemetry or request payload.
                source = ReplaceOnce(source,
                    "var qp={none:Aa,minimal:Aa,low:Oa,medium:Ea,high:wa,xhigh:Sa};",
                    "var qp={none:Aa,minimal:Aa,low:Oa,medium:Ea,high:wa,xhigh:Sa,max:Sa,ultra:Sa};");
                // VSAI runs in the current local workspace. Keep the location picker
                // hidden for follow-ups too: upstream only auto-hides it before a
                // conversation ID exists when there is no Git repository.
                source = ReplaceOnce(source,
                    "hideRunLocationDropdown:jl,showWorkspaceDropdown:F&&!G,",
                    "hideRunLocationDropdown:!0,showWorkspaceDropdown:F&&!G,");
                break;
            case LabelModule:
                source = ReplaceOnce(source,
                    "function d(e){let t=(0,u.c)(6),{effort:n}=e;switch(n){",
                    "function d(e){let t=(0,u.c)(6),{effort:n}=e;switch(n){case`max`:return`Max`;case`ultra`:return`Ultra`;");
                break;
            case SettingsModule:
                source = ReplaceOnce(source,
                    "function R(e,t){return(e===`none`||e===`minimal`||e===`low`||e===`medium`||e===`high`||e===`xhigh`)&&t.includes(e)?e:k}",
                    "function R(e,t){return(e===`none`||e===`minimal`||e===`low`||e===`medium`||e===`high`||e===`xhigh`||e===`max`||e===`ultra`)&&t.includes(e)?e:k}");
                // The saved model/effort query must follow the same manual refresh
                // policy as the catalog, including both of its UI observers.
                source = ReplaceOnce(source,
                    "queryKey:[...N,t,n],staleTime:_.FIVE_MINUTES",
                    "queryKey:[...N,t,n],staleTime:Infinity,refetchOnWindowFocus:!1,refetchOnReconnect:!1,refetchInterval:!1");
                break;
            case ModelQueriesModule:
                // Load once, then refresh only when an explicit catalog/config change
                // invalidates the query. Focus and elapsed time must preserve selection.
                source = ReplaceOnce(source,
                    "staleTime:u.FIVE_MINUTES,queryFn:()=>i(`list-models-for-host`",
                    "staleTime:Infinity,refetchOnWindowFocus:!1,refetchOnReconnect:!1,refetchInterval:!1,queryFn:()=>i(`list-models-for-host`");
                break;
        }

        // Moving a module changes its base URL. Anchor every asset literal (including
        // Vite's lazy-import/preload arrays and CSS) to the original immutable bundle.
        // Absolute imports still pass through the import map for the guarded adapters.
        source = RelativeAssetLiteral.Replace(source,
            match => match.Groups[1].Value + AssetBaseUrl + match.Groups[2].Value + match.Groups[1].Value);
        return source;
    }

    internal static string ReplaceOnce(string source, string before, string after)
    {
        var index = source.IndexOf(before, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("The frozen Codex compatibility adapter does not match exactly once.");
        }

        return source.Substring(0, index) + after + source.Substring(index + before.Length);
    }
}
