namespace GitWork.Core;

public sealed record ChangedFile(string Status, string Path, string? OriginalPath)
{
    public string DisplayPath => this.OriginalPath is null ? this.Path : $"{this.OriginalPath} → {this.Path}";

    public string StatusDescription => this.Status.Contains('U') || this.Status is "AA" or "DD" ? "Конфликт"
        : this.Status.Contains('D') ? "Удалён"
        : this.Status.Contains('R') ? "Переименован"
        : this.Status.Contains('C') ? "Скопирован"
        : this.Status.Contains('A') || this.Status == "??" ? "Добавлен"
        : this.Status.Contains('M') ? "Изменён"
        : this.Status.Contains('T') ? "Изменён тип"
        : this.Status;
}
