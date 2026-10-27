using Stillbar.Core.Controls;

namespace Stillbar.Tests;

public class ControlsTests
{
    [Fact]
    public void Phonebook_sections_are_connection_names() =>
        Assert.Equal(["Работа", "Home VPN"], Vpn.PhonebookNames("[Работа]\r\nMEDIA=rastapi\r\n\r\n[Home VPN]\nType=2\n[работа]\n"));

    [Theory]
    [InlineData("No connections\r\nCommand completed successfully.\r\n")]
    [InlineData("Нет подключений\r\nКоманда успешно завершена.\r\n")]
    [InlineData("")]
    public void Nothing_connected_in_any_language(string output) => Assert.Empty(Vpn.ConnectedNames(output));

    [Fact]
    public void Connected_names_are_the_middle_lines() =>
        Assert.Equal(["Работа"], Vpn.ConnectedNames("Подключено к\r\nРабота\r\nКоманда успешно завершена.\r\n"));

    [Fact]
    public void Confirm_gate_needs_the_same_action_twice_in_time()
    {
        var t = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var g = new ConfirmGate(TimeSpan.FromSeconds(4));
        Assert.False(g.Click("restart", t));
        Assert.Equal("restart", g.Armed(t.AddSeconds(1)));
        Assert.False(g.Click("shutdown", t.AddSeconds(1)));   // a different action re-arms instead
        Assert.True(g.Click("shutdown", t.AddSeconds(2)));
        Assert.Null(g.Armed(t.AddSeconds(2)));
        Assert.False(g.Click("restart", t.AddSeconds(3)));
        Assert.Null(g.Armed(t.AddSeconds(8)));                 // expired
    }

    [Fact]
    public void Exclusions_ignore_case_spaces_and_exe() =>
        Assert.True(Exclusions.Matches(Exclusions.Parse(" OBS64.exe, zoom\nPowerPnt "), "powerpnt"));

    [Fact]
    public void Exclusions_do_not_match_parts_of_names() =>
        Assert.False(Exclusions.Matches(["code"], "codex"));
}
