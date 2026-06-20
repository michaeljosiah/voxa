namespace Voxa.Audio.Onnx.Tests;

public class OnnxDeviceParserTests
{
    [Theory]
    [InlineData(null, OnnxDevice.Cpu)]
    [InlineData("", OnnxDevice.Cpu)]
    [InlineData("   ", OnnxDevice.Cpu)]
    [InlineData("cpu", OnnxDevice.Cpu)]
    [InlineData("CPU", OnnxDevice.Cpu)]
    [InlineData("auto", OnnxDevice.Auto)]
    [InlineData("Auto", OnnxDevice.Auto)]
    [InlineData("cuda", OnnxDevice.Cuda)]
    [InlineData("CUDA", OnnxDevice.Cuda)]
    [InlineData(" cuda ", OnnxDevice.Cuda)]
    [InlineData("directml", OnnxDevice.DirectML)]
    [InlineData("dml", OnnxDevice.DirectML)]
    [InlineData("coreml", OnnxDevice.CoreML)]
    public void Parses_known_device_strings_case_insensitively(string? value, OnnxDevice expected)
    {
        Assert.True(OnnxDeviceParser.TryParse(value, out var device));
        Assert.Equal(expected, device);
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("vulkan")]   // a whisper.cpp device, not an ORT EP
    [InlineData("nonsense")]
    public void Rejects_unknown_or_numeric_device_strings(string value)
    {
        Assert.False(OnnxDeviceParser.TryParse(value, out var device));
        Assert.Equal(OnnxDevice.Cpu, device); // out stays at the safe default
    }

    [Fact]
    public void Valid_values_are_the_five_known_spellings()
    {
        Assert.Equal(new[] { "cpu", "auto", "cuda", "directml", "coreml" }, OnnxDeviceParser.ValidValues);
    }
}
