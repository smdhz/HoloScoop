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
    public int ExpectedSpeakerCount { get; set; }
    public float ClusteringThreshold { get; set; } = 0.5f;
    public double MinimumSubtitleOverlapRatio { get; set; } = 0.6;
}
