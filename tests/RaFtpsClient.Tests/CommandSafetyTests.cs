namespace RaFtpsClient.Tests;

public class CommandSafetyTests
{
    // A lone CR or LF is accepted as a terminator by most servers, so filtering only CRLF let a
    // remote name smuggle a second command onto the control channel.
    [Theory]
    [InlineData("DELE evil\r\nRETR /etc/passwd")]
    [InlineData("DELE evil\nRETR /etc/passwd")]
    [InlineData("DELE evil\rRETR /etc/passwd")]
    [InlineData("CWD \n")]
    public void RejectsEmbeddedLineBreaks(string command)
    {
        Assert.Throws<FTPException>(() => FTPSClient.CheckCommandInjection(command));
    }

    [Theory]
    [InlineData("RETR report.txt")]
    [InlineData("CWD /home/user/my documents")]
    [InlineData("DELE naïve-café.txt")]
    [InlineData("NOOP")]
    public void AcceptsOrdinaryCommands(string command)
    {
        FTPSClient.CheckCommandInjection(command);
    }

    [Fact]
    public void MasksThePasswordArgument()
    {
        Assert.Equal("PASS ****", FTPSClient.MaskCredentials("PASS hunter2"));
    }

    [Fact]
    public void MasksThePasswordArgumentRegardlessOfCase()
    {
        Assert.Equal("PASS ****", FTPSClient.MaskCredentials("pass hunter2"));
    }

    [Theory]
    [InlineData("USER alice")]
    [InlineData("RETR passwords.txt")]
    [InlineData("PASSIVE-ish")]
    public void LeavesOtherCommandsIntact(string command)
    {
        Assert.Equal(command, FTPSClient.MaskCredentials(command));
    }
}
