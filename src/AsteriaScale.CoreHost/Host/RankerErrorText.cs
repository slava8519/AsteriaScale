namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// The Russian status-line text of a sense-ranker error (rule 9). Core passes
/// the worker's and the selector's codes through unchanged, and their own
/// messages are English or carry absolute paths, so the app must never show
/// them; this text names the code instead, so the owner can still look it up
/// in the log, where the original message goes as <c>detail</c>. A code this
/// table does not know is shown as «Ошибка модели значений (code)» rather than
/// hidden. No sentence contains a path or the internal word for the host.
/// </summary>
internal static class RankerErrorText
{
    private static readonly Dictionary<string, string> Sentences = new(StringComparer.Ordinal)
    {
        // The selector's own codes (GlossBertDictionarySelectorService).
        ["worker_missing"] = "Модель значений: не найден исполняемый файл воркера",
        ["model_directory_missing"] = "Модель значений: не найден каталог моделей",
        ["worker_start_failed"] = "Модель значений: воркер не запустился",
        ["worker_not_running"] = "Модель значений: воркер неожиданно завершился",
        ["worker_eof"] = "Модель значений: воркер неожиданно завершился",
        ["worker_response_timeout"] = "Модель значений не ответила вовремя",
        ["invalid_worker_json"] = "Модель значений ответила в неверном формате",
        ["protocol_violation"] = "Модель значений нарушила протокол обмена",
        ["unsupported_provider_model"] = "Выбранная модель значений не работает на этом устройстве",
        ["worker_error"] = "Модель значений сообщила об ошибке",

        // The worker's codes (src/AsteriaScale.RankerWorker).
        ["npu_provider_unavailable"] = "NPU недоступен на этой платформе; модель значений работает только на CPU",
        ["consec_bundle_missing"] = "Модель значений ConSeC не установлена",
        ["consec_manifest_missing"] = "Модель значений ConSeC не установлена: нет описания бандла",
        ["consec_manifest_invalid"] = "Бандл модели значений ConSeC повреждён",
        ["consec_model_missing"] = "Модель значений ConSeC установлена не полностью",
        ["consec_tokenizer_missing"] = "Модель значений ConSeC установлена не полностью",
        ["consec_artifact_path_unsafe"] = "Каталог модели значений ConSeC должен быть настоящим каталогом, а не символической ссылкой",
        ["consec_model_integrity_failed"] = "Файл модели значений ConSeC не совпадает с закреплённой версией",
        ["consec_tokenizer_integrity_failed"] = "Файл модели значений ConSeC не совпадает с закреплённой версией",
        ["consec_tokenizer_load_failed"] = "Модель значений ConSeC не загрузилась",
        ["consec_tokenizer_incompatible"] = "Модель значений ConSeC несовместима с этой версией приложения",
        ["model_missing"] = "Модель значений не установлена",
        ["tokenizer_missing"] = "Модель значений установлена не полностью",
        ["model_load_failed"] = "Модель значений не загрузилась",
        ["model_not_ready"] = "Модель значений ещё не готова",
        ["tokenizer_not_ready"] = "Модель значений ещё не готова",
        ["cpu_model_integrity_failed"] = "Файл модели значений не совпадает с закреплённой версией",
        ["tokenizer_integrity_failed"] = "Файл модели значений не совпадает с закреплённой версией",
        ["embedding_bundle_integrity_failed"] = "Файл модели значений не совпадает с закреплённой версией",
        ["unsupported_model"] = "Эта модель значений не поддерживается",
        ["unsupported_cpu_model"] = "Эта модель значений не поддерживается",
        ["inference_failed"] = "Модель значений не смогла выбрать значение",
    };

    public static string For(string code) =>
        Sentences.TryGetValue(code, out string? sentence)
            ? $"{sentence} ({code})."
            : $"Ошибка модели значений ({code}).";
}
