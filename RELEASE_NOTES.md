# Git Work 0.4.0

## Новые возможности

- Stash для репозиториев и worktree: сохранение изменений, отдельный просмотр рабочей версии и индекса, применение и удаление сохранений.
- Первая публикация локальной ветки: выбор сервера и имени, создание новой удалённой ветки и автоматическая настройка upstream. Существующие ветки на сервере не перезаписываются.
- Контекстные меню репозиториев, коммитов, файлов и веток: сведения, история, копирование путей, открытие в IDE и проводнике, правила .gitignore и удаление свободных локальных веток.
- «Отменить все…» для выбранного репозитория: подтверждение перед выполнением и резервная копия рабочих и подготовленных версий до начала отмены. Вложенные репозитории, worktree и игнорируемые файлы сохраняются.

## Исправления и интерфейс

- Исправлен коммит большого количества файлов на Windows: список путей передаётся через файл, без ограничения длины командной строки. Сохраняется независимое содержимое индекса.
- Исправлено быстрое переключение вкладок и открытие контекстного меню с первого правого клика.
- При запуске открываются «Репозитории»; сообщения о загрузке выводятся в нижней строке состояния.
- Полная версия файла в истории использует общую подсветку синтаксиса C#.
- Вложенные репозитории и worktree можно фиксировать как Git-ссылки на коммит, без включения их внутренних файлов.
- Управление расположено рядом с выбором ветки. До настройки upstream вместо Pull/Push показывается публикация ветки; Stash, IDE, терминал и папка перенесены в меню «…».

## Features

- Stash changes across repository worktrees, preview working-tree and index versions separately, apply or drop saved changes.
- Publish a local branch to a chosen remote and configure upstream automatically. Existing remote branches are never overwritten.
- Context menus for repositories, commits, files and branches, including information, file history, paths, IDE/Explorer, ignore rules and local branch deletion.
- Discard all ordinary file changes with explicit confirmation and backups of working and staged versions before any changes are discarded.
- Commit large file selections on Windows without hitting the command-line length limit, while preserving unrelated staged changes.
- Reliable tab navigation, repository-first startup, C# colors in file-history snapshots and a compact toolbar beside the branch selector.
