namespace FileVault.Core;

public interface IDriveItem : IFolderItem
{
    long TotalSize { get; }
    long TotalFreeSpace { get; }

    /// <summary>
    /// Является ли диск "пользовательским" (стоит показывать в UI по умолчанию).
    /// Клиент сам решает, фильтровать по этому признаку или нет.
    /// </summary>
    bool IsUserVisible { get; }
}
