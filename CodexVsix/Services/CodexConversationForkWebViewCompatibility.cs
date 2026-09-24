namespace CodexVsix.Services;

// Applied only to the hash-checked runtime copy, never to the frozen assets.
internal static class CodexConversationForkWebViewCompatibility
{
    internal const string ManagerModule = "app-server-manager-signals-B8yJv95l.js";

    internal const string ForkOptionsBefore = @"sideConversationParentNavigationPath:u}){let d=e.getConversation(t);";
    internal const string ForkOptionsAfter = @"sideConversationParentNavigationPath:u,lastTurnId:vsaiLastTurnId}){let d=e.getConversation(t);";
    internal const string ForkRequestBefore = @"persistExtendedHistory:!1}),h=f(m.thread.id);";
    internal const string ForkRequestAfter = @"persistExtendedHistory:!1,...vsaiLastTurnId==null?{}:{lastTurnId:vsaiLastTurnId}}),h=f(m.thread.id);";

    internal const string ForkFromTurnBefore = @"async function Cx(e,{sourceConversationId:t,targetTurnId:n,cwd:r,workspaceRoots:i,collaborationMode:a}){let o=e.getConversation(t);if(!o)throw Error(`Source conversation not found.`);let s=o.turns.findIndex(e=>e.turnId===n);if(s===-1)throw Error(`Target turn not found.`);let c=o.turns.length-s-1,l=await e.forkConversationFromLatest({sourceConversationId:t,cwd:r,workspaceRoots:i,collaborationMode:a,addForkedSyntheticItem:!1}),u=e.getConversation(l);if(!u)throw Error(`Forked conversation state not found.`);return c>0&&Sx(e,{conversationId:l,conversationState:u,rollbackResponse:await e.sendRequest(`thread/rollback`,{threadId:l,numTurns:c})}),A_(e,l,t,_x(o)),l}";

    // Paginated threads reject thread/rollback. Fork through the selected ID in
    // one server operation; retain the existing resume/hydration and navigation.
    internal const string ForkFromTurnAfter = @"async function Cx(manager,{sourceConversationId,targetTurnId,cwd,workspaceRoots,collaborationMode}) {
    const source = manager.getConversation(sourceConversationId);
    if (!source) throw Error(`Source conversation not found.`);
    if (!source.turns.some(turn => turn.turnId === targetTurnId)) throw Error(`Target turn not found.`);
    const forkId = await manager.forkConversationFromLatest({
        sourceConversationId, cwd, workspaceRoots, collaborationMode,
        lastTurnId: targetTurnId, addForkedSyntheticItem: false
    });
    const fork = manager.getConversation(forkId);
    if (!fork) throw Error(`Forked conversation state not found.`);
    const lastTurn = fork.turns.filter(turn => turn.turnId != null).at(-1);
    if (lastTurn?.turnId !== targetTurnId) throw Error(`Codex did not preserve the requested fork history boundary.`);
    A_(manager, forkId, sourceConversationId, _x(source));
    return forkId;
}";

    internal static string AdaptModule(string source)
    {
        source = CodexReasoningEffortWebViewCompatibility.ReplaceOnce(source, ForkOptionsBefore, ForkOptionsAfter);
        source = CodexReasoningEffortWebViewCompatibility.ReplaceOnce(source, ForkRequestBefore, ForkRequestAfter);
        return CodexReasoningEffortWebViewCompatibility.ReplaceOnce(source, ForkFromTurnBefore, ForkFromTurnAfter);
    }
}
