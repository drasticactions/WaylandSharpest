using Microsoft.CodeAnalysis;
using VerifyXunit;
using Xunit;

namespace WaylandSharpest.Generator.Tests;

public class GeneratorTests
{
    private const string SampleProtocol = """
        <?xml version="1.0" encoding="UTF-8"?>
        <protocol name="sample">
          <interface name="sample_thing" version="3">
            <description summary="a sample interface for snapshot testing"/>
            <enum name="mode">
              <entry name="slow" value="0" summary="slow mode"/>
              <entry name="fast" value="1" summary="fast mode"/>
            </enum>
            <enum name="flags" bitfield="true">
              <entry name="a" value="1"/>
              <entry name="b" value="2"/>
            </enum>
            <request name="set_mode">
              <arg name="mode" type="uint" enum="mode"/>
            </request>
            <request name="make_child">
              <arg name="id" type="new_id" interface="sample_child"/>
              <arg name="label" type="string" allow-null="true"/>
            </request>
            <request name="give_data">
              <arg name="data" type="array"/>
              <arg name="fd" type="fd"/>
            </request>
            <request name="convert" type="destructor">
              <arg name="id" type="new_id" interface="sample_child"/>
            </request>
            <request name="destroy" type="destructor"/>
            <event name="state">
              <arg name="serial" type="uint"/>
              <arg name="position" type="fixed"/>
              <arg name="child" type="object" interface="sample_child" allow-null="true"/>
            </event>
            <event name="spawned" since="2">
              <arg name="child" type="new_id" interface="sample_child"/>
            </event>
          </interface>
          <interface name="sample_child" version="1">
            <request name="destroy" type="destructor"/>
          </interface>
        </protocol>
        """;

    [Fact]
    public async Task Sample_protocol_snapshot()
    {
        var (result, _) = GeneratorTestHelper.Run(("sample.xml", SampleProtocol));

        var source = Assert.Single(result.Results).GeneratedSources
            .Single(s => s.HintName == "sample.g.cs");
        await Verifier.Verify(source.SourceText.ToString(), extension: "cs").UseDirectory("Snapshots");
    }

    [Fact]
    public void Sample_protocol_compiles()
    {
        var (result, output) = GeneratorTestHelper.Run(("sample.xml", SampleProtocol));

        Assert.Empty(result.Diagnostics);
        AssertCompiles(output);
    }

    [Fact]
    public void Core_and_xdg_shell_compile_together()
    {
        var wayland = File.ReadAllText(GeneratorTestHelper.FixturePath("wayland.xml"));
        var xdgShell = File.ReadAllText(GeneratorTestHelper.FixturePath("xdg-shell.xml"));

        var (result, output) = GeneratorTestHelper.Run(
            ("wayland.xml", wayland),
            ("xdg-shell.xml", xdgShell));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, Assert.Single(result.Results).GeneratedSources.Length);
        AssertCompiles(output);
    }

    [Fact]
    public void Xdg_shell_resolves_core_interfaces_from_referenced_assembly()
    {
        var xdgShell = File.ReadAllText(GeneratorTestHelper.FixturePath("xdg-shell.xml"));

        var (result, output) = GeneratorTestHelper.Run(("xdg-shell.xml", xdgShell));

        // wl_surface, wl_seat, wl_output resolve against WaylandSharpest.dll, so
        // no WLSG002 unresolved-reference warnings may appear.
        Assert.Empty(result.Diagnostics);
        var text = Assert.Single(result.Results).GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("global::Wayland.WlSurface", text);
        AssertCompiles(output);
    }

    [Fact]
    public void Malformed_protocol_reports_diagnostic()
    {
        var (result, _) = GeneratorTestHelper.Run(
            ("bad.xml", "<protocol><interface version=\"1\"/></protocol>"));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("WLSG001", diagnostic.Id);
    }

    [Fact]
    public void Unrelated_xml_is_ignored()
    {
        var (result, _) = GeneratorTestHelper.Run(
            ("app.config.xml", "<configuration><appSettings/></configuration>"));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }

    private static void AssertCompiles(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }
}
