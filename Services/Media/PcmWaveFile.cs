using System.Buffers.Binary;

namespace HoloScoop.Services.Media;

internal sealed record PcmWaveData(int SampleRate, float[] Samples);

internal static class PcmWaveFile
{
    public static PcmWaveData Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (ReadFourCc(reader) != "RIFF" || reader.ReadUInt32() < 4 || ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");
        }

        ushort format = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        byte[]? audio = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadUInt32();
            if (chunkSize > int.MaxValue || stream.Position + chunkSize > stream.Length)
            {
                throw new InvalidDataException($"'{path}' contains an invalid WAVE chunk.");
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException($"'{path}' contains an invalid fmt chunk.");
                }
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                stream.Position += chunkSize - 16;
            }
            else if (chunkId == "data")
            {
                audio = reader.ReadBytes((int)chunkSize);
            }
            else
            {
                stream.Position += chunkSize;
            }

            if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
            {
                stream.Position++;
            }
        }

        if (format != 1 || channels != 1 || sampleRate <= 0 || bitsPerSample != 16 || audio is null)
        {
            throw new InvalidDataException(
                $"'{path}' must contain mono 16-bit PCM audio, but format={format}, channels={channels}, rate={sampleRate}, bits={bitsPerSample}.");
        }

        var samples = new float[audio.Length / 2];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(index * 2, 2)) / 32768f;
        }
        return new PcmWaveData(sampleRate, samples);
    }

    private static string ReadFourCc(BinaryReader reader) =>
        new(reader.ReadChars(4));
}
