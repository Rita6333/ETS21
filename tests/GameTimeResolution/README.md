# Game clock resolution regression

This console runner source-links the production `GameTimeService.cs`. It allocates
a small synthetic PE image inside **its own Windows x64 process** and exercises the
real layout resolver and clock reader through reflection. The resolver reads only
that allocation through the current-process pseudo handle. It never calls
`Probe`, `Apply`, `Restore`, or discovers or accesses a game process.

The fake image keeps the historical environment refresh RVA while moving the
independent clock global, reproducing the ETS2 1.62.3.2 regression. Checks cover
original and patched environment code, cached empty-world recovery, conflicting
signatures, process/base/size cache identity and historical offset fallback.

From the workspace root:

```powershell
.\_deps\dotnet8\sdk\dotnet.exe run --project tests/GameTimeResolution/GameTimeResolution.csproj
```

An optional argument filters checks by name. To compare the same checks against
an earlier source file, set `-p:GameTimeServiceSource=ABSOLUTE_PATH_TO_SOURCE`.
The runner exits nonzero when a check fails, or with exit code 2 on platforms that
cannot run these Windows x64 native-memory checks.
