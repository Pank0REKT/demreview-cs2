namespace DemReview.Application;

/// <summary>Платформенно-зависимые операции. Реализации — для Windows и Linux.</summary>
public interface IPlatformService
{
    /// <summary>Отображаемое имя платформы.</summary>
    string PlatformName { get; }

    /// <summary>Каталог данных пользователя (кеш, настройки, логи).</summary>
    string AppDataDirectory { get; }

    /// <summary>Каталог с изображениями карт рядом с приложением.</summary>
    string MapsDirectory { get; }

    /// <summary>Файл иконки окна в Assets: .ico для Windows, .png для Linux.</summary>
    string WindowIconFileName { get; }

    /// <summary>Сравнение путей с учётом регистрозависимости файловой системы.</summary>
    StringComparer PathComparer { get; }

    /// <summary>Ключ пути для хранилища: на Windows регистр не важен, на Linux важен.</summary>
    string NormalizePathKey(string fullPath);

    /// <summary>Открывает каталог в файловом менеджере ОС.</summary>
    void OpenFolder(string path);

    /// <summary>Открывает https-ссылку в браузере по умолчанию.</summary>
    void OpenUrl(string url);
}
