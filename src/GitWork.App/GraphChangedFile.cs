using GitWork.Core;

namespace GitWork.App;

public sealed record GraphChangedFile(ChangedFile File)
{
    public string DisplayPath => this.File.DisplayPath;

    public string StatusCode => this.File.StatusCode;

    public string StatusLabel => L10n.T(this.StatusCode switch
    {
        "A" => "Добавлен",
        "D" => "Удалён",
        "R" => "Переименован",
        "C" => "Скопирован",
        "T" => "Изменён тип",
        "U" => "Конфликт",
        _ => "Изменён"
    });
}
