# Game clock resolution regression

This console runner source-links the production `GameTimeService.cs`. It allocates
a small synthetic PE image inside **its own Windows x64 process** and exercises the
real layout resolver and clock reader through reflection. The resolver reads only
that allocation through the current-process pseudo handle. Session lifecycle
checks seed `Probe` with a read-only handle to this runner's own process and the
synthetic image. They never discover or access a game process, or call `Apply`
or `Restore`.

The fake image keeps the historical environment refresh RVA while moving the
independent clock global, reproducing the ETS2 1.62.3.2 regression. Checks cover
original and patched environment code, cached empty-world recovery, conflicting
signatures, process/base/size cache identity, historical offset fallback, failed
scan retry delay, zero-allocation cached reads and concurrent session disposal.

From the workspace root:

```powershell
.\_deps\dotnet8\sdk\dotnet.exe run --project tests/GameTimeResolution/GameTimeResolution.csproj
```

An optional argument filters checks by name. `--benchmark` runs 20,000 warmed
cached reads of the synthetic clock and reports elapsed time and managed
allocations. On the development machine the v1.1.4 reader allocated 168 bytes
per sample; the optimized reader allocates zero. Timing is affected by OS
scheduling and is not an application-wide CPU benchmark. A source with a
compatible API can be selected using
`-p:GameTimeServiceSource=ABSOLUTE_PATH_TO_SOURCE`.
The runner exits nonzero when a check fails, or with exit code 2 on platforms that
cannot run these Windows x64 native-memory checks.
