using Bunit;
using Microsoft.AspNetCore.Components;

namespace FFXIV_Craft_Architect.Tests;

public class WebComponentTestSmoke : BunitContext
{
    [Fact]
    public void Bunit_RendersBasicMarkup()
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "probe");
            builder.AddContent(2, "ready");
            builder.CloseElement();
        };
        var rendered = Render(fragment);

        Assert.Equal("ready", rendered.Find(".probe").TextContent);
    }
}
