using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var settingsDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitWork-ui-settings-" + Guid.NewGuid().ToString("N"));
        typeof(AppSettings).GetProperty("SettingsDirectoryOverride", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, settingsDirectory);
        if (AppSettings.Load().RootPaths.Count != 0 || AppSettings.Load().Workspaces.Count != 0)
        {
            throw new InvalidOperationException("Изолированный запуск не должен читать пользовательские настройки.");
        }

        Program.CheckNavigation();
        var isPreview = args.Length == 2 && args[0] is "--preview" or "--compare" or "--graph" or "--graph-dark";
        var application = Program.CreateApplication(isPreview);

        if (args.Contains("--preparation-check", StringComparer.Ordinal))
        {
            PreparationUiChecks.Run();
            application.Shutdown();
            return;
        }

        if (args.Length == 2 && args[0] == "--preparation-preview")
        {
            PreparationUiChecks.RunPreview(application, args[1]);
            application.Shutdown();
            return;
        }

        if (args.Contains("--repository-check", StringComparer.Ordinal))
        {
            RepositoryClientUiChecks.Run();
            application.Shutdown();
            return;
        }

        if (args.Length == 2 && args[0] == "--design-preview")
        {
            DesignPreview.Run(application, args[1]);
            application.Shutdown();
            return;
        }

        if (args.Length == 3 && args[0] == "--showcase")
        {
            Showcase.Run(application, args[1], args[2]);
            return;
        }

        if (isPreview)
        {
            ThemeManager.Apply(args[0] == "--graph-dark" ? "Dark" : "Light");
            var git = new GitService();
            var path = System.IO.Path.GetFullPath(args[1]);
            Window preview;

            if (args[0] is "--graph" or "--graph-dark")
            {
                preview = new GitGraphWindow([new GraphRepositoryChoice(System.IO.Path.GetFileName(path), path)], git);
            }
            else if (args[0] == "--compare")
            {
                preview = new BranchComparisonWindow(path, git);
            }
            else
            {
                var files = git.GetStatusAsync(path, true).GetAwaiter().GetResult();
                preview = new ChangesWindow("Проверка просмотра изменений", [new ChangeSource("Пример", path, files)], git, files.FirstOrDefault()?.Path, path);
            }

            preview.ShowDialog();
            application.Shutdown();
            return;
        }

        if (args.Contains("--baseline", StringComparer.Ordinal))
        {
            var currentStyle = (Style)application.FindResource(typeof(ListBox));
            var previousStyle = new Style(typeof(ListBox));

            foreach (var setter in currentStyle.Setters.OfType<Setter>().Where(setter => setter.Property != Control.TemplateProperty))
            {
                previousStyle.Setters.Add(new Setter(setter.Property, setter.Value));
            }

            application.Resources[typeof(ListBox)] = previousStyle;
        }

        Program.CheckDiffView();
        UsabilityChecks.Run();
        DailyChecks.Run();
        GraphUiChecks.Run();
        Program.CheckChangeScopes();
        SelectedCommitUiChecks.Run();
        InlineWorktreeUiChecks.Run();
        RepositoryClientUiChecks.Run();
        BundleActionsUiChecks.Run();
        LocalizationUiChecks.Run();
        UpdateServiceChecks.Run();
        PreparationUiChecks.Run();

        var window = new MainWindow();
        var root = (Grid)window.Content;
        var workspace = (Grid)window.FindName("Workspace");
        ((FrameworkElement)window.FindName("DetailPanel")).IsEnabled = true;
        var repositoryList = (TreeView)window.FindName("RepositoryTree");
        var filesList = (ListBox)window.FindName("FilesList");
        var repository = new RepositoryInfo("Online", @"C:\Projects\Online", "Projects");
        repositoryList.ItemsSource = new[] { new NavigationNode { Key = "repository", Kind = "Repository", Title = "Online" } };
        filesList.ItemsSource = new[] { new ChangedFile(" M", "Example.cs", null) };

        foreach (var isEnabled in new[] { true, false, true, false })
        {
            workspace.IsEnabled = isEnabled;
            root.Measure(new Size(1380, 860));
            root.Arrange(new Rect(0, 0, 1380, 860));
            root.UpdateLayout();

            foreach (var list in new Control[] { repositoryList, filesList })
            {
                Program.AssertDarkBackground(list, isEnabled);
            }
        }

        Console.WriteLine("Успешно: списки сохраняют тёмный фон при блокировке и повторном включении управления.");
        Program.CheckThemes(window);
        window.Close();
        application.Shutdown();
    }

    private static void CheckThemes(MainWindow window)
    {
        var background = (SolidColorBrush)window.FindResource("BackgroundBrush");
        var added = (SolidColorBrush)window.FindResource("DiffAddedBrush");
        var original = background.Color;
        var originalAdded = added.Color;
        var existingLabel = (TextBlock)window.FindName("RepositoryTitle");

        ThemeManager.Apply("Light");
        window.UpdateLayout();
        var lightBackground = (SolidColorBrush)window.Background;
        var lightText = (SolidColorBrush)existingLabel.Foreground;
        var lightAdded = (SolidColorBrush)window.FindResource("DiffAddedBrush");

        if (lightBackground.Color == original || lightAdded.Color == originalAdded
            || lightBackground.Color.R < 230 || lightText.Color.R > 100)
        {
            throw new InvalidOperationException("Светлая тема должна обновлять уже созданные окна, текст и diff.");
        }

        ThemeManager.Apply("Dark");

        if (((SolidColorBrush)window.Background).Color != original || ((SolidColorBrush)window.FindResource("DiffAddedBrush")).Color != originalAdded)
        {
            throw new InvalidOperationException("Возврат к тёмной теме должен восстанавливать цвета существующих элементов.");
        }

        if (((Menu)window.FindName("MainMenu")).Items.Count != 4)
        {
            throw new InvalidOperationException("Верхнее меню должно содержать «Файл», «Правка», «Вид» и «Справка».");
        }

        var branchDialog = new CreateBranchWindow(@"C:\repo", [new BranchInfo("main", "refs/heads/main", false, @"C:\other")], "main");
        var sourcePicker = (ComboBox)branchDialog.FindName("StartPointPicker");

        if (sourcePicker.Items.Count != 2 || branchDialog.StartPoint != "refs/heads/main")
        {
            throw new InvalidOperationException("Исходная ветка для новой ветки доступна, даже если она занята другим worktree.");
        }

        branchDialog.Close();
        Console.WriteLine("OK: темы переключают существующие окна и diff; меню и выбор исходной ветки доступны.");
    }

    private static Application CreateApplication(bool isPreview)
    {
        var preview = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resourcePath = System.IO.Path.GetFullPath("src/GitWork.App/App.xaml");
        var document = System.Xml.Linq.XDocument.Load(resourcePath);
        var dictionary = document.Descendants(System.Xml.Linq.XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation")).First();
        var context = new System.Windows.Markup.ParserContext { BaseUri = new Uri(resourcePath) };
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        preview.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString(), context);

        return preview;
    }

    private static void CheckDiffView()
    {
        var file = new ChangeFileEntry(new ChangeSource("repo", @"C:\repo", []), new ChangedFile("MM", "example.cs", null));
        var notifications = new List<string?>();
        file.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        file.IsSelected = true;
        file.SelectedScope = ChangeScope.Staged;

        if (!file.IsStagedSelected || !notifications.Contains(nameof(ChangeFileEntry.IsStagedSelected))
            || !notifications.Contains(nameof(ChangeFileEntry.IsWorkingSelected)))
        {
            throw new InvalidOperationException("Выделение файла и слоя должно обновлять привязки интерфейса.");
        }

        var view = new DiffView { Name = "DiffProbe" };
        var unifiedHunkChanges = new List<int>();
        view.UnifiedHunkChanged += unifiedHunkChanges.Add;
        var patch = "@@ -1 +1 @@\n-old\n+new\n" + string.Join("\n", Enumerable.Repeat(" context", 120)) + "\n@@ -200 +200 @@\n-before\n+after";
        view.ShowDiff(new FileDiff("", "", patch,
            [new DiffLine(1, "old short", 1, "new " + new string('x', 240), DiffLineKind.Modified),
             new DiffLine(2, new string('y', 240), 2, "new short", DiffLineKind.Modified)], false, "Часть diff"));
        view.Measure(new Size(900, 500));
        view.Arrange(new Rect(0, 0, 900, 500));
        view.UpdateLayout();
        var code = (RichTextBox)view.FindName("CodeViewer");
        var table = code.Document.Blocks.OfType<Table>().Single();
        if (table.Columns.Count != 2 || table.RowGroups[0].Rows.Any(row => row.Cells.Count != 2))
        {
            throw new InvalidOperationException("Обе версии должны занимать одинаковые колонки с общими строками таблицы.");
        }

        code.SelectAll();
        if (!code.Selection.Text.Contains("old short") || !code.Selection.Text.Contains("new short"))
        {
            throw new InvalidOperationException("Код нескольких строк должен выделяться как текст.");
        }

        if (((Border)view.FindName("MessageBorder")).Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("Пояснение о частичном diff должно быть видно рядом со строками.");
        }

        view.IsUnified = true;
        view.UpdateLayout();
        var parsedRows = view.UnifiedLines.ToArray();
        var removedIndex = Array.FindIndex(parsedRows, row => row.Kind == UnifiedDiffLineKind.Removed && row.Text == "old");
        var addedIndex = Array.FindIndex(parsedRows, row => row.Kind == UnifiedDiffLineKind.Added && row.Text == "new");

        var addedRow = code.Document.Blocks.OfType<Paragraph>().FirstOrDefault(paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("+ new"));
        if (addedRow?.Background != view.FindResource("DiffAddedBrush"))
        {
            throw new InvalidOperationException("Добавленная строка выделяемого diff должна сохранять цвет фона.");
        }

        if (removedIndex < 0 || addedIndex <= removedIndex || parsedRows[removedIndex].OldNumber != 1
            || parsedRows[removedIndex].NewNumber is not null || parsedRows[removedIndex].Marker != "−"
            || parsedRows[addedIndex].OldNumber is not null || parsedRows[addedIndex].NewNumber != 1
            || parsedRows[addedIndex].Marker != "+" || parsedRows[0].Kind != UnifiedDiffLineKind.Hunk)
        {
            throw new InvalidOperationException("Единый diff должен показывать удаление перед добавлением, оба номера строк и заголовок блока.");
        }

        ((Button)view.FindName("NextChangeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)view.FindName("NextChangeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        view.UpdateLayout();

        if (view.CurrentUnifiedHunkIndex != 1 || code.Visibility != Visibility.Visible
            || ((CheckBox)view.FindName("UnifiedToggle")).IsChecked != true)
        {
            throw new InvalidOperationException("Переход к следующему блоку должен синхронно выбирать блок единого diff.");
        }

        view.IsUnified = false;
        if (view.CurrentUnifiedHunkIndex != 0 || unifiedHunkChanges.Last() != 0)
        {
            throw new InvalidOperationException("Смена режима должна сообщить staging выбранный hunk после сброса навигации.");
        }

        view.IsUnified = true;
        if (view.CurrentUnifiedHunkIndex != 0 || unifiedHunkChanges.Last() != 0)
        {
            throw new InvalidOperationException("Программное включение единого diff должно передать правильный hunk в staging.");
        }

        var untrackedDiff = new FileDiff("", "новая строка", "+ новая строка",
            [new DiffLine(null, "", 1, "новая строка", DiffLineKind.Added)], false, null);
        view.ShowDiff(untrackedDiff);
        var untrackedRows = view.UnifiedLines.ToArray();

        if (untrackedRows.Length != 2 || untrackedRows[0].Kind != UnifiedDiffLineKind.Hunk
            || untrackedRows[1].Kind != UnifiedDiffLineKind.Added || untrackedRows[1].Text != "новая строка"
            || untrackedRows[1].NewNumber != 1 || view.UnifiedHunkCount != 1)
        {
            throw new InvalidOperationException("Единый diff должен показывать новый файл без git hunk заголовков.");
        }

        const string metadataPatch = "diff --git a/old.cs b/new.cs\nsimilarity index 90%\nrename from old.cs\nrename to new.cs\nold mode 100644\nnew mode 100755";
        view.ShowDiff(new FileDiff("", "", metadataPatch, [], false,
            "Изменены метаданные файла; содержимое не менялось. Подробности приведены в тексте diff."));
        var metadataRows = view.UnifiedLines.ToArray();

        if (metadataRows.Length != 6 || metadataRows.Any(row => row.Kind != UnifiedDiffLineKind.Notice)
            || !metadataRows.Any(row => row.Text == "rename from old.cs")
            || !metadataRows.Any(row => row.Text == "new mode 100755"))
        {
            throw new InvalidOperationException("Единый diff должен сохранять пути переименования и метаданные режима файла.");
        }

        view.ShowDiff(GitChangeService.ParseUnifiedDiff(metadataPatch + "\n@@ -1 +1 @@\n-old\n+new"));
        if (!view.UnifiedLines.Any(row => row.Text == "new mode 100755")
            || !view.UnifiedLines.Any(row => row.Text == "rename to new.cs")
            || view.UnifiedHunkCount != 1 || view.CurrentUnifiedHunkIndex != 0)
        {
            throw new InvalidOperationException("Изменение текста не должно скрывать смену режима и переименование файла.");
        }

        view.ShowDiff(new FileDiff("", "", "Бинарный файл. Текстовый diff недоступен.", [], true, "Бинарный файл."));
        if (view.UnifiedLines.Any(row => row.Text.Contains("Бинарный файл", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Предупреждение о бинарном файле не должно дублироваться в списке diff.");
        }

        const string csharp = "var message = \"escaped \\\"quote\\\"\"; // комментарий";
        view.FilePath = "example.cs";
        view.ShowDiff(new FileDiff("", csharp, "+ " + csharp,
            [new DiffLine(null, "", 1, csharp, DiffLineKind.Added)], false, null));
        var highlighted = code.Document.Blocks.OfType<Paragraph>().Last().Inlines.OfType<Run>().ToArray();
        if (!string.Concat(highlighted.Select(run => run.Text)).Contains(csharp, StringComparison.Ordinal)
            || !highlighted.Any(run => run.Text == "\"escaped \\\"quote\\\"\"" && run.Foreground == view.FindResource("SyntaxStringBrush"))
            || ((Run)view.FindName("AddedLinesText")).Text != "+1"
            || ((Run)view.FindName("RemovedLinesText")).Text.Length != 0)
        {
            throw new InvalidOperationException("Подсветка C# должна сохранять экранированные строки и исходный текст, а статистика — показывать реальные изменения.");
        }

        Program.AssertDarkBackground(view, true);
        Console.WriteLine("OK: выделение слоя, ровные колонки, unified-строки и навигация по блокам.");
        view.ShowDiff(new FileDiff("", "", "", [], false, "Бинарный файл"));

        if (view.Lines.Count != 0 || ((Button)view.FindName("NextChangeButton")).IsEnabled
            || ((Grid)view.FindName("ColumnHeaders")).Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("Пустой diff не должен показывать фиктивные строки и действия с блоками.");
        }
    }

    private static void CheckChangeScopes()
    {
        var git = new GitService();
        var first = new ChangeSource("Первый", @"C:\first", [new ChangedFile("MM", "same.cs", null), new ChangedFile("??", "new.cs", null), new ChangedFile("M ", "staged.cs", null)]);
        var second = new ChangeSource("Второй", @"C:\second", [new ChangedFile("MM", "same.cs", null)]);
        var window = new ChangesWindow("Комплект", [first, second], git, "same.cs", second.Path);
        var files = (ListBox)window.FindName("FilesList");
        var repository = (ComboBox)window.FindName("RepositoryPicker");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(820, 460));
        content.Arrange(new Rect(0, 0, 820, 460));
        content.UpdateLayout();
        var commitButton = (Button)window.FindName("CommitButton");
        if (commitButton.TranslatePoint(new Point(), content).Y + commitButton.ActualHeight > 460 || files.ActualHeight < 45)
        {
            throw new InvalidOperationException($"Форма коммита и список файлов должны помещаться в невысоком окне. Кнопка: {commitButton.TranslatePoint(new Point(), content).Y}+{commitButton.ActualHeight}, список: {files.ActualHeight}.");
        }

        var status = (TextBlock)window.FindName("CommitStatusText");
        status.Text = string.Join(" ", Enumerable.Repeat("Сообщение Git об ошибке.", 10));
        status.Visibility = Visibility.Visible;
        ((ProgressBar)window.FindName("CommitProgress")).Visibility = Visibility.Visible;
        content.UpdateLayout();
        if (commitButton.TranslatePoint(new Point(), content).Y + commitButton.ActualHeight > 460)
        {
            throw new InvalidOperationException($"Длинная ошибка и индикатор не должны вытеснять кнопку коммита за окно: {commitButton.TranslatePoint(new Point(), content).Y}+{commitButton.ActualHeight}.");
        }

        status.Visibility = Visibility.Collapsed;
        ((ProgressBar)window.FindName("CommitProgress")).Visibility = Visibility.Collapsed;
        if (files.Items.Count != 1 || files.SelectedItem is not ChangeFileEntry selected || selected.Source.Path != second.Path)
        {
            throw new InvalidOperationException("Первый выбранный файл должен сохранять точный репозиторий при одинаковых именах.");
        }

        repository.SelectedIndex = 0;

        if (files.Items.Count != 3 || !files.Items.Cast<ChangeFileEntry>().Any(file => file.File.Path == "staged.cs"))
        {
            throw new InvalidOperationException("Единый список должен включать подготовленные, рабочие и новые файлы выбранного репозитория.");
        }

        var title = (TextBox)window.FindName("CommitTitleBox");
        var description = (TextBox)window.FindName("CommitDescriptionBox");
        title.Text = "Первый черновик";
        description.Text = "Описание";
        var excluded = files.Items.Cast<ChangeFileEntry>().Single(file => file.File.Path == "staged.cs");
        excluded.IsIncluded = false;
        if (((TextBlock)window.FindName("CommitSelectionText")).Text != "Выбрано 2 из 3"
            || ((CheckBox)window.FindName("SelectAllBox")).IsChecked is not null
            || !((Button)window.FindName("CommitButton")).IsEnabled)
        {
            throw new InvalidOperationException("Галочка должна менять состав коммита и общий счётчик.");
        }

        repository.SelectedIndex = 1;
        title.Text = "Второй черновик";
        repository.SelectedIndex = 0;
        if (title.Text != "Первый черновик" || description.Text != "Описание" || excluded.IsIncluded)
        {
            throw new InvalidOperationException("Выбор и черновики разных репозиториев должны сохраняться отдельно.");
        }

        ((TextBox)window.FindName("SearchBox")).Text = "нет-такого-файла";

        if (files.Items.Count != 0 || files.SelectedItem is not null || ((DiffView)window.FindName("DiffViewer")).Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("Поиск без результатов не должен оставлять подробности скрытого файла.");
        }

        window.Close();
        if (((TextBlock)window.FindName("CommitSelectionText")).Text != "Выбрано 2 из 3")
        {
            throw new InvalidOperationException("Поиск не должен менять состав коммита.");
        }

        Console.WriteLine("OK: единый список включает staged-only, галочки и черновики независимы по репозиториям, поиск не меняет выбор.");
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Program.VisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void CheckNavigation()
    {
        var online = new RepositoryInfo("Online", @"C:\repos\Online", "repos");
        var shared = new RepositoryInfo("Shared", @"C:\repos\Shared", "repos");
        var first = new WorktreeInfo(online.Path, "a", "dev", true, false, false, false, null);
        var linked = new WorktreeInfo(@"D:\tasks\51818\Online", "b", "task/51818", false, false, false, false, null);
        var second = new WorktreeInfo(@"D:\tasks\51818\Shared", "c", "task/51818", false, false, false, false, null);
        var workspace = new ProjectWorkspace
        {
            Id = "task",
            Name = "Задача 51818",
            ProjectId = "removed-legacy-group",
            WorktreePaths = new Dictionary<string, string> { [online.Path] = linked.Path, [shared.Path] = second.Path }
        };
        var registered = new Dictionary<string, IReadOnlyList<WorktreeInfo>>
        {
            [online.Path] = [first, linked],
            [shared.Path] = [second]
        };
        var all = NavigationBuilder.Build([online, shared], registered, [workspace], "", "All");
        var copies = NavigationBuilder.Flatten(all).Where(node => node.Kind == "Worktree").ToArray();
        if (copies.Length != 3 || copies.Select(node => node.Worktree!.Path).Distinct().Count() != 3)
        {
            throw new InvalidOperationException("В навигации потеряна или продублирована рабочая копия.");
        }

        if (NavigationBuilder.Flatten(all).Count(node => node.Kind == "Workspace") != 1)
        {
            throw new InvalidOperationException("Комплект без старой группы должен оставаться доступным.");
        }

        var search = NavigationBuilder.Build([online, shared], registered, [workspace], "task/51818", "All");
        if (NavigationBuilder.Flatten(search).Count(node => node.Kind == "Worktree") != 2)
        {
            throw new InvalidOperationException("Поиск по ветке должен находить все копии внутри комплекта.");
        }

        var linkedOnly = NavigationBuilder.Build([online, shared], registered, [workspace], "", "All", linkedOnly: true);
        if (NavigationBuilder.Flatten(linkedOnly).Any(node => node.Worktree?.IsMain == true))
        {
            throw new InvalidOperationException("Раздел Worktree не должен показывать основные копии среди отдельных worktree.");
        }

        var loose = NavigationBuilder.Build([online, shared], registered, [workspace], "", "Loose");
        if (NavigationBuilder.Flatten(loose).Single(node => node.Kind == "Worktree").Worktree?.IsMain != true)
        {
            throw new InvalidOperationException("Основная копия должна быть доступна отдельно от комплекта.");
        }

        if (loose.Single().Children.Single().Kind != "Worktree" || loose.Single().Count != 1)
        {
            throw new InvalidOperationException("Одиночная копия должна открываться без лишнего раскрытия, сохраняя счётчик раздела.");
        }

        var multiple = NavigationBuilder.Build([online], new Dictionary<string, IReadOnlyList<WorktreeInfo>> { [online.Path] = [first, linked] }, [], "", "All");

        if (NavigationBuilder.Flatten(multiple).Single(node => node.Kind == "Repository").Children.Count != 2
            || NavigationBuilder.Flatten(multiple).Count(node => node.Kind == "Worktree") != 2)
        {
            throw new InvalidOperationException("Несколько копий одного репозитория должны сохранять группировку.");
        }

        var duplicate = online with { Path = @"D:\other\Online" };
        var repeated = NavigationBuilder.Build([online, duplicate], new Dictionary<string, IReadOnlyList<WorktreeInfo>>
        {
            [online.Path] = [first],
            [duplicate.Path] = [first with { Path = duplicate.Path }]
        }, [], "", "Loose");

        if (NavigationBuilder.Flatten(repeated).Where(node => node.Kind == "Worktree").Select(node => node.Subtitle).Distinct().Count() != 2)
        {
            throw new InvalidOperationException("Одинаковые имена репозиториев в разных папках должны быть различимы.");
        }

        Console.WriteLine("OK: навигация показывает все копии, не дублирует комплект и сохраняет старые наборы.");

        var other = new ProjectWorkspace { Id = "other", Name = "Другой комплект" };
        var favorites = NavigationBuilder.Build([online, shared], registered, [workspace, other], "", "Favorites", [workspace.Id], [other.Id]);
        if (NavigationBuilder.Flatten(favorites).Where(node => node.Kind == "Workspace").Single().Workspace?.Id != workspace.Id
            || NavigationBuilder.Flatten(favorites).Count(node => node.Kind == "Worktree") != 2)
        {
            throw new InvalidOperationException("Избранное должно показывать только выбранный комплект и его точные копии.");
        }

        var recent = NavigationBuilder.Build([online, shared], registered, [workspace, other], "", "Recent", [workspace.Id], [other.Id, workspace.Id]);
        if (!NavigationBuilder.Flatten(recent).Where(node => node.Kind == "Workspace").Select(node => node.Workspace!.Id).SequenceEqual([other.Id, workspace.Id]))
        {
            throw new InvalidOperationException("Недавние комплекты должны сохранять порядок открытий, независимо от избранного.");
        }

        var conflicted = new WorkspaceRepositoryRow
        {
            Name = "Online",
            Path = first.Path,
            RepositoryPath = first.Path,
            Worktree = first,
            ChangeCount = 2,
            ChangedFiles = [new ChangedFile("UU", "conflict.cs", null), new ChangedFile(" M", "changed.cs", null)]
        };
        if (conflicted.ConflictCount != 1 || !conflicted.StatusLabel.Contains("Конфликтов: 1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Сводка должна отличать конфликты от обычных изменений.");
        }

        Console.WriteLine("OK: избранное, порядок недавних и конфликты в сводке комплекта.");
    }
    private static void AssertDarkBackground(Control list, bool isEnabled)
    {
        var width = Math.Max(1, (int)Math.Ceiling(list.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(list.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(list);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var brightPixels = 0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 220 && pixels[i + 1] > 220 && pixels[i + 2] > 220 && pixels[i + 3] > 220)
            {
                brightPixels++;
            }
        }

        var ratio = (double)brightPixels / (width * height);

        if (ratio > 0.05)
        {
            throw new InvalidOperationException($"Светлый фон в {list.Name}, IsEnabled={isEnabled}: {ratio:P1}.");
        }

        Console.WriteLine($"OK: {list.Name}, IsEnabled={isEnabled}, светлых пикселей {ratio:P2}.");
    }
}
