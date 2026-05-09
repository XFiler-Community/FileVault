namespace FileVault.Core;

/// <summary>
/// Опциональный интерфейс. Только LocalFileProvider реализует его через Process.Start + UseShellExecute.
/// Cloud-провайдеры НЕ реализуют. Проверяй через `item is IOpenableItem openable`.
/// </summary>
public interface IOpenableItem
{
    void Open();
}
