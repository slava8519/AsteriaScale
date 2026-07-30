namespace AsteriaScale.Services;

public sealed record SmartDictionarySelection(
    int Index,
    string Translation,
    double Confidence,
    string ContextTranslation);
