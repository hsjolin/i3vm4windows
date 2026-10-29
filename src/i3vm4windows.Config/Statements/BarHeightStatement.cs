namespace i3vm4windows.Config.Statements;

/// <summary>height n: the bar's height in pixels.</summary>
public sealed class BarHeightStatement : BarDirectiveStatement
{
    public override string Name => "height";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 && int.TryParse(parameters[0], out var height))
        {
            if (height is < 16 or > 200)
            {
                context.Warn($"Bar height out of range: {context.Line}");
                return;
            }

            context.Bar.Height = height;
            return;
        }

        context.Warn($"Unsupported bar height: {context.Line}");
    }
}
