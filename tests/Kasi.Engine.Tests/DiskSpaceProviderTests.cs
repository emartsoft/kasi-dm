namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-008")]
public sealed class DiskSpaceProviderTests
{
    [Fact]
    public void WindowsDiskSpaceProvider_returns_positive_available_bytes_for_existing_directory()
    {
        var provider = WindowsDiskSpaceProvider.Instance;

        var ok = provider.TryGetAvailableFreeBytes(AppContext.BaseDirectory, out var availableBytes);

        ok.Should().BeTrue();
        availableBytes.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WindowsDiskSpaceProvider_returns_false_for_blank_or_null_directory(string path)
    {
        var provider = WindowsDiskSpaceProvider.Instance;

        provider.TryGetAvailableFreeBytes(path, out var availableBytes).Should().BeFalse();
        availableBytes.Should().Be(0);

        provider.TryGetAvailableFreeBytes(null!, out var nullBytes).Should().BeFalse();
        nullBytes.Should().Be(0);
    }

    [Fact]
    public void WindowsDiskSpaceProvider_returns_false_for_nonexistent_directory()
    {
        var provider = WindowsDiskSpaceProvider.Instance;
        var missing = Path.Combine(Path.GetTempPath(), $"kasi-missing-{Guid.NewGuid():N}", "sub");

        var ok = provider.TryGetAvailableFreeBytes(missing, out var availableBytes);

        ok.Should().BeFalse();
        availableBytes.Should().Be(0);
    }

    [Fact]
    public void DiskStatus_Success_has_no_error_kind_code_or_detail()
    {
        var status = DiskStatus.Success;

        status.IsSuccess.Should().BeTrue();
        status.ErrorKind.Should().BeNull();
        status.NativeErrorCode.Should().BeNull();
        status.Detail.Should().BeNull();
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), 112)]
    [InlineData(unchecked((int)0x80070027), 39)]
    [InlineData(unchecked((int)0x8007050F), 1295)]
    [InlineData(unchecked((int)0x80070718), 1816)]
    [InlineData(unchecked((int)0xC000007F), null)]
    [InlineData(unchecked((int)0xC0000044), null)]
    public void DiskStatus_FromException_maps_disk_full_and_quota_codes_to_NotEnoughSpace_and_redacts_path(
        int hResult,
        int? expectedNativeCode)
    {
        var ex = new IOException(@"Disk full writing C:\Users\ secret_user \Downloads\archive.kasipart", hResult);

        var status = DiskStatus.FromException(ex, @"C:\Users\secret_user\Downloads\archive.kasipart");

        status.IsSuccess.Should().BeFalse();
        status.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);
        status.NativeErrorCode.Should().Be(expectedNativeCode);
        status.Detail.Should().Contain("archive.kasipart")
            .And.NotContain("secret_user")
            .And.NotContain("Downloads");
    }

    [Fact]
    public void DiskStatus_FromException_maps_other_IO_errors_to_IoError_and_redacts_path()
    {
        var ex = new IOException(@"Sharing violation on C:\Users\alice\Downloads\video.kasipart", unchecked((int)0x80070020));

        var status = DiskStatus.FromException(ex, @"C:\Users\alice\Downloads\video.kasipart");

        status.IsSuccess.Should().BeFalse();
        status.ErrorKind.Should().Be(DiskErrorKind.IoError);
        status.NativeErrorCode.Should().Be(32);
        status.Detail.Should().Contain("video.kasipart").And.NotContain("alice");
    }

    [Fact]
    public void PartFileOpenFailure_rejects_successful_DiskStatus_and_exposes_ErrorKind()
    {
        var act = () => new PartFileOpenFailure(DiskStatus.Success);
        act.Should().Throw<ArgumentException>();

        var failure = new PartFileOpenFailure(DiskStatus.Failure(DiskErrorKind.NotEnoughSpace, "low space", 112));
        failure.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);
        failure.Status.NativeErrorCode.Should().Be(112);
    }
}
