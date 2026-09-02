namespace HoloScoop.Services.Media;

public sealed class SpeakerDiarizationOptions
{
    public const string SectionName = "SpeakerDiarization";

    public bool Enabled { get; set; } = true;
    public string SegmentationModelPath { get; set; } =
        "/opt/sherpa-onnx/speaker-segmentation/model.onnx";
    public string EmbeddingModelPath { get; set; } =
        "/opt/sherpa-onnx/nemo_en_titanet_large.onnx";
    public int NumThreads { get; set; } = 12;
    public double MinimumSubtitleOverlapRatio { get; set; } = 0.6;
    public double VoiceMatchThreshold { get; set; } = 0.65;
    public double VoiceMatchMinimumMargin { get; set; } = 0.05;
}
