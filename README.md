# MemCleaner

Маленькая утилита очистки памяти Windows: подрезает рабочие наборы всех процессов,
чистит standby/modified page lists и сбрасывает файловый кэш
(тот же механизм, что у Mem Reduct, + трей и автозагрузка).

## Сборка

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

## Использование

```
MemCleaner.exe                 разовая чистка (консоль, UAC спросит)
MemCleaner.exe --watch         цикл чисток в консоли
MemCleaner.exe --tray          иконка в трее + авто-чистка

  --interval N  / -i N         пауза между проверками, сек (60 по умолчанию)
  --threshold N / -t N         чистить только когда свободно <= N% (25 по умолчанию; 0 = всегда)

MemCleaner.exe --install       добавить в автозагрузку (Task Scheduler, elevated, без UAC при логине)
MemCleaner.exe --uninstall     убрать из автозагрузки
MemCleaner.exe --status        показать задачу автозапуска
```

В трее: правый клик — **Clean now / Pause / Exit**, в подсказке — сколько освободилось последний раз.
Лог трей-режима: `%LOCALAPPDATA%\MemCleaner\log.txt`.
