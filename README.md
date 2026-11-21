# MemCleaner

Small Windows memory cleaner: trims working sets of all processes,
purges the standby/modified page lists and flushes the file system cache
(same mechanism as Mem Reduct).

## Build

`dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out`

## Usage

```
MemCleaner.exe            # one-shot clean (asks for admin via UAC)
MemCleaner.exe --watch 60 # repeat every 60 seconds
```
