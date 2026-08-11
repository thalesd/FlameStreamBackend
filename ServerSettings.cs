namespace SagaBackend;

public record ServerSettings(
    string LibraryRoot,
    string CacheRoot,
    int HlsSegmentSeconds,
    string HardwareAcceleration
);
