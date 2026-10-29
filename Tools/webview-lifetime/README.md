# WebView teardown regression

`Content.Tests.Client.WebViewLifetimeTest` exercises the real engine UI tree and
the stock `WebViewControl` with a tracked browser implementation. It verifies
ancestor removal, nested removal, disposal before deferred detach, duplicate close and the actual native-close
callback, rather than only removal from the content registry.

Run the fast cases after building `Content.Client`:

```powershell
dotnet test Content.Tests/Content.Tests.csproj --no-restore -c Debug -p:BuildProjectReferences=false --filter 'FullyQualifiedName~WebViewLifetimeTest&FullyQualifiedName!~NativeCefProcesses'
```

The Windows native case is opt-in. Set `DEEPLAGOON_CEF_MODULE` to the installed,
verified official WebView module directory and `DEEPLAGOON_CEF_REPORT` to an
absolute ignored scratch directory:

```powershell
$env:DEEPLAGOON_CEF_MODULE = '<installed module directory>'
$env:DEEPLAGOON_CEF_REPORT = "$PWD/.local/codex/webview-lifetime/native"
dotnet test Content.Tests/Content.Tests.csproj --no-build --no-restore -c Debug --filter FullyQualifiedName~NativeCefProcessesRemainBoundedAfterAncestorExit
```

The test uses that module's subprocess and libcef, the engine's CEF app, a unique
cache directory and local data documents. The browser implementation proxy starts
real CEF browsers; the real stock UI control decides whether to call native close.
Windows process counts include only children of the test host. No installed
module is modified and no process belonging to the running game is closed.

One run compares twelve ancestor removals with the old detach sequence and twelve
with `WebViewLifetime.Close`, including disposal of the view or its parent before
the deferred queue runs. It verifies native `OnBeforeClose` callbacks and
writes browser/process counts to `native-processes.json`. The baseline's test
browsers are explicitly closed before measuring the corrected sequence; all test
browsers are closed before CEF shutdown.

Shared CEF services and a spare renderer may outlive individual browsers. The
fixed count is checked against a small allowance, not against zero OS processes.
This is a native lifecycle smoke test, not a complete gameplay or long-session
verification of an updated official client.
