namespace Conversation;

public interface ITranscriptSink {
    void AppendUserLine(string text);
    void AppendSystemLine(string text);
    void BeginAssistantLine();
    void AppendAssistantDelta(string delta);
    void FinalizeAssistantLine();
}

public interface IStandeeController {
    Task SetSpriteAsync(string fileName, CancellationToken ct = default);
    Task ShowAsync(CancellationToken ct = default);
    Task HideAsync(CancellationToken ct = default);
}
