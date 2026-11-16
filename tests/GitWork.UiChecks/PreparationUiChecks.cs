using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class PreparationUiChecks
{
    public static void RunPreview(Application application, string outputDirectory)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "git-work-preparation-preview-" + Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(fixtureRoot, "TechnoKad", "Online");
        Directory.CreateDirectory(repositoryPath);
        Directory.CreateDirectory(outputDirectory);
        var profiles = PreparationUiChecks.PreviewProfiles();
        try
        {
            var repository = new RepositoryInfo("Online", @"G:\work\TechnoKad\Online", "TechnoKad");
            var branch = new BranchInfo("master", "refs/heads/master", false, null);
            var choices = new[] { new ProjectWorktreeChoice(repository, [branch], branch) };
            var createWindow = new CreateProjectWorkspaceWindow(new ProjectGroup { Name = "TechnoKad" }, choices,
                new AppSettings { PreparationProfiles = profiles }) { Width = 850, Height = 850 };
            var createSelector = (PreparationSelector)createWindow.FindName("Preparation");
            ((ComboBox)createSelector.FindName("ProfilePicker")).SelectedItem = profiles[0];
            ((CheckBox)createSelector.FindName("RunCheck")).IsChecked = true;
            ((CheckBox)createSelector.FindName("GroupCheck")).IsChecked = true;
            ((TextBox)createWindow.FindName("WorkspaceNameBox")).Text = "test12";
            ((TextBox)createWindow.FindName("BranchNameBox")).Text = "feature/test12";
            ((TextBox)createWindow.FindName("TargetFolderBox")).Text = @"G:\work\TechnoKad\workspaces\Online-test12";
            createWindow.Show();
            PreparationUiChecks.Capture(createWindow, Path.Combine(outputDirectory, "preparation-create.png"));
            createWindow.Close();

            var editor = new PreparationProfilesWindow(profiles, profiles[0].Id);
            editor.Show();
            PreparationUiChecks.Capture(editor, Path.Combine(outputDirectory, "preparation-profile.png"));
            editor.Close();

            var runRoot = Path.Combine(fixtureRoot, "Online-test12");
            Directory.CreateDirectory(runRoot);
            var failedProfile = new PreparationProfile
            {
                Name = profiles[0].Name,
                Steps =
                [
                    new PreparationStep { Name = "Сборка Debug", Shell = "PowerShell", Command = "Write-Output 'Пакеты восстановлены'" },
                    new PreparationStep { Name = "Подключение ресурсов", Shell = "PowerShell", Command = "Write-Output 'Ресурсы подключены'" },
                    new PreparationStep { Name = "Установка конфигурации", Shell = "PowerShell", Command = "Write-Output 'Не найден файл Online\\Files\\Configs\\development.7z'; exit 1" }
                ]
            };
            var runWindow = new PreparationRunWindow(failedProfile, runRoot);
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                runWindow.Show();
                await PreparationUiChecks.WaitUntilAsync(() => !runWindow.IsRunning && runWindow.Result is not null);
                ((TextBox)runWindow.FindName("RootPathText")).Text = @"G:\work\TechnoKad\workspaces\Online-test12";
                PreparationUiChecks.Capture(runWindow, Path.Combine(outputDirectory, "preparation-run-failed.png"));
                runWindow.Close();
            });
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(fixtureRoot, true);
        }
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-preparation-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PreparationUiChecks.CheckProfileEditor();
            PreparationUiChecks.CheckSettingsAndSelection(root);
            PreparationUiChecks.CheckCreationForms(root);
            PreparationUiChecks.CheckPreparationRunAsync(root);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, true);
        }

        Console.WriteLine("OK: профили подготовки редактируются отдельно от исходных настроек, выбираются по пути, а запуск поддерживает retry и отмену.");
    }

    private static void CheckProfileEditor()
    {
        var source = new List<PreparationProfile>
        {
            PreparationUiChecks.Profile("Online · Debug", "Сборка", "Write-Output ready")
        };
        var originalName = source[0].Name;
        var originalCommand = source[0].Steps[0].Command;
        var canceledEditor = new PreparationProfilesWindow(source);
        ((TextBox)canceledEditor.FindName("ProfileNameBox")).Text = "Отмена";
        ((TextBox)canceledEditor.FindName("CommandBox")).Text = "Изменение без сохранения";
        canceledEditor.Close();
        if (source[0].Name != originalName || source[0].Steps[0].Command != originalCommand)
        {
            throw new InvalidOperationException("Закрытие редактора без сохранения изменило исходный профиль.");
        }

        var editor = new PreparationProfilesWindow(source);
        if (((ComboBox)editor.FindName("ShellPicker")).SelectedValue as string != "PowerShell")
        {
            throw new InvalidOperationException("Редактор должен сразу показывать сохранённую оболочку PowerShell.");
        }

        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            ((TextBox)editor.FindName("ProfileNameBox")).Text = "Online · Release";
            ((TextBox)editor.FindName("StepNameBox")).Text = "Сборка Release";
            ((TextBox)editor.FindName("CommandBox")).Text = "Write-Output release";
            typeof(PreparationProfilesWindow).GetMethod("Save_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editor, [null, new RoutedEventArgs()]);
        }));
        var result = editor.ShowDialog();
        var saved = editor.Profiles.Single();
        if (result != true || saved.Name != "Online · Release" || saved.Steps[0].Command != "Write-Output release"
            || saved.Steps[0].Shell != "PowerShell"
            || source[0].Name != originalName || source[0].Steps[0].Command != originalCommand)
        {
            throw new InvalidOperationException("Редактор не сохранил новые поля профиля из изолированной копии.");
        }

    }

    private static void CheckSettingsAndSelection(string root)
    {
        var repository = Path.Combine(root, "TechnoKad", "Online");
        var group = Path.GetDirectoryName(repository)!;
        var worktree = Path.Combine(root, "workspaces", "Online-test");
        var secret = "test-only-secret-" + Guid.NewGuid().ToString("N");
        var profile = PreparationUiChecks.Profile("Online · Debug", "Сборка", "Write-Output ready");
        profile.Variables.Add(new PreparationVariable
        {
            Name = "CONFIG_PASSWORD",
            Value = PreparationSecrets.Protect(secret),
            IsSecret = true
        });
        var settings = new AppSettings { PreparationProfiles = [profile] };
        settings.PreparationBindings.Add(new PreparationBinding
        {
            Scope = "Group",
            Path = group,
            ProfileId = profile.Id,
            RunAutomatically = true
        });
        settings.Save();
        var persistedJson = File.ReadAllText(AppSettings.FilePath);
        var reloaded = AppSettings.Load();
        var restoredSecret = reloaded.PreparationProfiles.Single().Variables.Single().Value;
        if (persistedJson.Contains(secret, StringComparison.Ordinal) || PreparationSecrets.Unprotect(restoredSecret) != secret
            || reloaded.PreparationBindings.Single().ProfileId != profile.Id)
        {
            throw new InvalidOperationException("Профиль или привязка не пережили перезапуск настроек либо секрет попал в settings открытым текстом.");
        }

        var selector = new PreparationSelector();
        selector.Configure(reloaded, repository);
        var picker = (ComboBox)selector.FindName("ProfilePicker");
        if (selector.SelectedProfile?.Id != profile.Id || ((CheckBox)selector.FindName("GroupCheck")).IsChecked != true
            || ((CheckBox)selector.FindName("RunCheck")).IsChecked != true || selector.ProfileToRun?.Id != profile.Id)
        {
            throw new InvalidOperationException("После перезапуска профиль группы не подставился в форму.");
        }

        var overrideProfile = PreparationUiChecks.Profile("Online · Release", "Сборка", "Write-Output release");
        reloaded.PreparationProfiles.Add(overrideProfile);
        reloaded.PreparationBindings.Add(new PreparationBinding
        {
            Scope = "Repository",
            Path = repository,
            ProfileId = overrideProfile.Id,
            RunAutomatically = false
        });
        selector.Configure(reloaded, repository);
        if (selector.SelectedProfile?.Id != overrideProfile.Id || ((CheckBox)selector.FindName("GroupCheck")).IsChecked == true
            || selector.ProfileToRun is not null || picker.Items.Count != 2)
        {
            throw new InvalidOperationException("Привязка репозитория не имеет приоритета над группой либо флажок отключения не сработал.");
        }

        ((CheckBox)selector.FindName("RunCheck")).IsChecked = true;
        ((CheckBox)selector.FindName("GroupCheck")).IsChecked = true;
        if (!selector.RememberSelection())
        {
            throw new InvalidOperationException("Выбранный профиль нельзя сохранить как значение по умолчанию для группы.");
        }

        var remembered = AppSettings.Load();
        if (remembered.PreparationBindings.Last().Scope != "Group" || remembered.PreparationBindings.Last().ProfileId != overrideProfile.Id)
        {
            throw new InvalidOperationException("Выбор профиля для группы не сохранился в настройках.");
        }

        selector.Configure(remembered, repository);
        ((CheckBox)selector.FindName("RunCheck")).IsChecked = false;
        if (!selector.RememberSelection())
        {
            throw new InvalidOperationException("Отключение подготовки для одного создания не прошло проверку формы.");
        }

        var nextCreate = new PreparationSelector();
        nextCreate.Configure(AppSettings.Load(), repository);
        if (((CheckBox)nextCreate.FindName("RunCheck")).IsChecked != true || nextCreate.SelectedProfile?.Id != overrideProfile.Id)
        {
            throw new InvalidOperationException("Разовое отключение изменило профиль или автозапуск группы по умолчанию.");
        }

        var worktreeProfile = PreparationUiChecks.Profile("Online · Local", "Локальный шаг", "Write-Output local");
        remembered.PreparationProfiles.Add(worktreeProfile);
        remembered.PreparationBindings.Add(new PreparationBinding
        {
            Scope = "Worktree",
            Path = worktree,
            ProfileId = worktreeProfile.Id,
            RunAutomatically = false
        });
        selector.Configure(remembered, repository, worktreePath: worktree);
        if (selector.SelectedProfile?.Id != worktreeProfile.Id || selector.ProfileToRun is not null)
        {
            throw new InvalidOperationException("Локальная привязка worktree не имеет приоритета либо выключенный запуск не отключён.");
        }

        selector.Configure(AppSettings.Load(), repository);
        if (selector.SelectedProfile?.Id != overrideProfile.Id || ((CheckBox)selector.FindName("RunCheck")).IsChecked != true)
        {
            throw new InvalidOperationException("Сохранённый профиль группы не подставился после повторной загрузки настроек.");
        }

        ((CheckBox)selector.FindName("RunCheck")).IsChecked = false;
        if (selector.ProfileToRun is not null)
        {
            throw new InvalidOperationException("Снятый флажок запуска всё ещё возвращает профиль для выполнения.");
        }
    }

    private static void CheckCreationForms(string root)
    {
        var repository = new RepositoryInfo("Online", Path.Combine(root, "TechnoKad", "Online"), "TechnoKad");
        var worktreeWindow = new CreateWorktreeWindow(repository, [], null, new AppSettings());
        var workspaceWindow = new CreateProjectWorkspaceWindow(new ProjectGroup { Name = "TechnoKad" },
            [new ProjectWorktreeChoice(repository, [], null)], new AppSettings());
        if (worktreeWindow.FindName("Preparation") is not PreparationSelector
            || workspaceWindow.FindName("Preparation") is not PreparationSelector)
        {
            throw new InvalidOperationException("Формы отдельного worktree и комплекта должны содержать выбор профиля подготовки.");
        }

        worktreeWindow.Close();
        workspaceWindow.Close();
        var other = new RepositoryInfo("Other", Path.Combine(root, "OtherGroup", "Other"), "OtherGroup");
        var profile = PreparationUiChecks.Profile("Primary profile", "Build", "Write-Output ready");
        var settings = new AppSettings
        {
            PreparationProfiles = [profile],
            PreparationBindings = [new PreparationBinding
            {
                Scope = "Group", Path = Path.GetDirectoryName(repository.Path)!, ProfileId = profile.Id
            }]
        };
        var primaryWindow = new CreateProjectWorkspaceWindow(new ProjectGroup { Name = "Online" },
            [new ProjectWorktreeChoice(other, [], null), new ProjectWorktreeChoice(repository, [], null)], settings, repository.Path);
        var preparation = (PreparationSelector)primaryWindow.FindName("Preparation");
        if (preparation.SelectedProfile?.Id != profile.Id || preparation.ProfileToRun?.Id != profile.Id)
        {
            throw new InvalidOperationException("Подготовка комплекта должна использовать выбранный основной репозиторий, а не первый в списке.");
        }

        primaryWindow.Close();
    }

    private static void CheckPreparationRunAsync(string root)
    {
        var runRoot = Path.Combine(root, "папка для запуска");
        Directory.CreateDirectory(runRoot);
        var profile = new PreparationProfile
        {
            Name = "Проверка повтора",
            Steps =
            [
                new PreparationStep { Name = "Первый успешный", Shell = "PowerShell", Command = "Add-Content first.txt done" },
                new PreparationStep
                {
                    Name = "Ошибка до повтора",
                    Shell = "PowerShell",
                    Command = "if (!(Test-Path failure.marker)) { Set-Content failure.marker first; exit 1 } else { Set-Content retry.marker second }"
                },
                new PreparationStep { Name = "После ошибки", Shell = "PowerShell", Command = "Set-Content final.marker done" }
            ]
        };
        var window = new PreparationRunWindow(profile, runRoot);
        InlineWorktreeUiChecks.RunAsync(async () =>
        {
            window.Show();
            await PreparationUiChecks.WaitUntilAsync(() => !window.IsRunning && window.Result is not null);
            if (window.Result is not { Success: false, FailedStepIndex: 1 } || !File.Exists(Path.Combine(runRoot, "first.txt"))
                || File.Exists(Path.Combine(runRoot, "retry.marker")) || File.Exists(Path.Combine(runRoot, "final.marker")))
            {
                throw new InvalidOperationException("Окно должно остановиться на ошибке шага, сохранив результат уже выполненных шагов.");
            }

            ((Button)window.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PreparationUiChecks.WaitUntilAsync(() => !window.IsRunning && window.Result is { Success: true });
            if (File.ReadAllLines(Path.Combine(runRoot, "first.txt")).Length != 1
                || !File.Exists(Path.Combine(runRoot, "retry.marker")) || !File.Exists(Path.Combine(runRoot, "final.marker")))
            {
                throw new InvalidOperationException("Повтор должен начать с ошибочного шага, пропустить успешный и продолжить профиль.");
            }

            window.Close();

            var cancellation = new PreparationRunWindow(new PreparationProfile
            {
                Name = "Проверка остановки",
                Steps = [new PreparationStep { Name = "Долгий шаг", Shell = "PowerShell", Command = "Start-Sleep -Seconds 30" }]
            }, runRoot);
            cancellation.Show();
            await PreparationUiChecks.WaitUntilAsync(() => cancellation.IsRunning);
            ((Button)cancellation.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PreparationUiChecks.WaitUntilAsync(() => !cancellation.IsRunning);
            if (cancellation.Result is not { IsCanceled: true } || ((ProgressBar)cancellation.FindName("Progress")).Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("Остановка должна завершить дочерний процесс, снять busy state и оставить результат отмены.");
            }

            cancellation.Close();
        });
    }

    private static PreparationProfile Profile(string name, string stepName, string command) => new()
    {
        Name = name,
        Steps = [new PreparationStep { Name = stepName, Shell = "PowerShell", Command = command }]
    };

    private static List<PreparationProfile> PreviewProfiles()
    {
        var profile = new PreparationProfile
        {
            Name = "Online · Debug",
            Steps =
            [
                new PreparationStep { Name = "Сборка Debug", Shell = "CMD", Command = @"Online\Files\Cmds\build.cmd debug", WorkingDirectory = "." },
                new PreparationStep { Name = "Подключение ресурсов", Shell = "CMD", Command = @"Online\Files\Cmds\link-resources.cmd", WorkingDirectory = "." },
                new PreparationStep { Name = "Установка конфигурации", Shell = "CMD", Command = "Online\\Files\\Configs\\install.cmd \"Online\\Files\\Configs\\development.7z\" debug other \"tm.land-%COMPUTERNAME%\" \"%CONFIG_PASSWORD%\"", WorkingDirectory = "." }
            ],
            Variables =
            [
                new PreparationVariable { Name = "COMPUTERNAME", Value = Environment.MachineName },
                new PreparationVariable { Name = "CONFIG_PASSWORD", Value = PreparationSecrets.Protect("preview-only-value"), IsSecret = true }
            ]
        };

        return [profile];
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Проверка окна подготовки не завершилась за отведённое время.");
            }

            await Task.Delay(25);
        }
    }
}
