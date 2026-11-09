namespace AsteriaScale.CoreHost.Protocol;

/// <summary>
/// Error bodies. <c>message</c> is Russian and may reach the user (rule 9);
/// it says «перевод» or «служба перевода», never the internal word for the
/// host (owner decision M15), and the subset's client rejects any message that
/// does. <c>detail</c> is English and for logs; codes and paths go there.
/// </summary>
internal static class Errors
{
    public static ErrorBody Create(string code, string message, string? detail = null) =>
        new() { Code = code, Message = message, Detail = detail };

    public static ErrorBody HelloRequired() =>
        Create(ErrorCodes.HelloRequired, "Служба перевода ещё не начала сеанс.", "core.hello must be the first request");

    public static ErrorBody BadRequest(string detail) =>
        Create(ErrorCodes.BadRequest, "Некорректный запрос к службе перевода.", detail);

    public static ErrorBody UnknownCommand(string command) =>
        Create(ErrorCodes.UnknownCommand, "Служба перевода не знает такой команды.", command);

    public static ErrorBody NotImplemented(string command, string stage) =>
        Create(ErrorCodes.NotImplemented, "Эта возможность пока недоступна.", $"{command} is reserved for {stage}");

    public static ErrorBody NotReady(string detail) =>
        Create(ErrorCodes.NotReady, "Служба перевода ещё не готова.", detail);

    public static ErrorBody Busy(int outstanding) =>
        Create(ErrorCodes.Busy, "Служба перевода перегружена запросами.", $"{outstanding} requests outstanding");

    public static ErrorBody ShuttingDown(string? detail = null) =>
        Create(ErrorCodes.ShuttingDown, "Служба перевода завершает работу.", detail);

    public static ErrorBody Cancelled() =>
        Create(ErrorCodes.Cancelled, "Запрос отменён.");

    public static ErrorBody Superseded(long newerId) =>
        new()
        {
            Code = ErrorCodes.Superseded,
            Message = "Запрос заменён новым наведением.",
            SupersededBy = newerId,
        };

    public static ErrorBody UnsupportedVersion(string detail) =>
        Create(ErrorCodes.UnsupportedVersion, "Версия службы перевода не подходит приложению.", detail);

    public static ErrorBody ShutdownTimeout(string detail) =>
        Create(ErrorCodes.ShutdownTimeout, "Служба перевода не завершила работу вовремя.", detail);

    public static ErrorBody Internal(string detail) =>
        Create(ErrorCodes.InternalError, "Внутренняя ошибка службы перевода.", detail);

    public static ErrorBody Cancelled(string detail) =>
        Create(ErrorCodes.Cancelled, "Запрос отменён.", detail);

    public static ErrorBody HoverDisabled() =>
        Create(ErrorCodes.HoverDisabled, "Перевод текста выключен — включите его в меню.", "hover translation is off");

    public static ErrorBody UnsupportedOnPlatform(string detail) =>
        Create(ErrorCodes.UnsupportedOnPlatform, "Эта возможность недоступна на macOS.", detail);

    public static ErrorBody InvalidSettings(string detail) =>
        Create(ErrorCodes.InvalidSettings, "Недопустимое значение настройки перевода.", detail);

    public static ErrorBody Conflict(long revision) =>
        Create(ErrorCodes.Conflict, "Настройки уже изменились; обновите их и повторите.", $"the current revision is {revision}");

    public static ErrorBody SettingsSaveFailed(string detail) =>
        Create(ErrorCodes.SettingsSaveFailed, "Не удалось сохранить настройки перевода.", detail);

    public static ErrorBody MorphologyMissing(string detail) =>
        Create(ErrorCodes.MorphologyMissing, "Не найден морфологический словарь перевода.", detail);

    public static ErrorBody DictionaryMissing(string detail) =>
        Create(ErrorCodes.DictionaryMissing, "Не найдены словари перевода.", detail);

    /// <summary>A rejected «Мой словарь» entry; Core's own Russian reason is the message.</summary>
    public static ErrorBody InvalidEntry(string message) =>
        Create(ErrorCodes.InvalidEntry, message, "the user dictionary rejected the entry");

    public static ErrorBody IoError(string detail) =>
        Create(ErrorCodes.IoError, "Не удалось прочитать или записать данные перевода.", detail);

    /// <summary>The Russian text of a connection-fatal fault.</summary>
    public static string FaultMessage(string code) => code switch
    {
        ErrorCodes.FrameTooLong => "Приложение отправило слишком длинный запрос; служба перевода закрывает соединение.",
        ErrorCodes.InvalidId => "Приложение нарушило нумерацию запросов; служба перевода закрывает соединение.",
        ErrorCodes.UnsupportedVersion => "Приложение использует другую версию протокола; служба перевода закрывает соединение.",
        ErrorCodes.ShutdownTimeout => "Служба перевода не завершила работу вовремя.",
        _ => "Приложение отправило некорректные данные; служба перевода закрывает соединение.",
    };
}

/// <summary>Thrown by a handler to answer its request with an error terminal.</summary>
internal sealed class RequestErrorException(ErrorBody body) : Exception(body.Code + ": " + body.Detail)
{
    public ErrorBody Body { get; } = body;
}
