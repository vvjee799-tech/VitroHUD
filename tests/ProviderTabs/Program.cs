using VitroHUD.Desktop;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}

foreach (var available in new[] { 330d, 380d, 560d })
{
    var width = ProviderTabInteraction.TabWidth(available);
    Check(Math.Abs(width * 3 + ProviderTabInteraction.Gap * 2 - available) < .001, "responsive slots fit");
    var state = new ProviderTabInteraction();
    state.Begin("Claude", 30, 20, 0);
    state.Move(34, 24, width);
    Check(state.End(true) == "Claude", "small motion remains click");

    state.Begin("Claude", 30, 20, 0);
    state.Move(available + 30, 20, width);
    Check(state.Order.SequenceEqual(new[] { "ChatGPT", "DeepSeek", "Claude" }), "drag first across both slots");
    Check(state.DragX == 2 * (width + ProviderTabInteraction.Gap), "drag constrained to strip");
    Check(!state.Hold(), "drag cannot become long press");
    Check(state.End(true) == null, "drop cannot click");

    state.Begin("Claude", available - 20, 20, state.Left("Claude", width));
    state.Move(-30, 20, width);
    Check(state.Order[0] == "Claude", "drag back to first slot");
    Check(state.End(false, cancel: true) == null && state.Order[2] == "Claude", "capture loss restores prior order without click");

    foreach (var name in new[] { "Claude", "ChatGPT", "DeepSeek" })
    {
        var order = state.Order.ToArray();
        state.Begin(name, 20, 20, state.Left(name, width));
        Check(state.Hold() && state.CollapsedProvider == name, "hold retains exact provider");
        Check(Math.Abs(state.Left(name, width) + width / 2 - available / 2) < .001, "held provider centered");
        Check(!state.Hold(), "same hold cannot toggle twice");
        state.Move(available, 20, width);
        Check(!state.Dragging && state.End(true) == null, "release after hold cannot drag or click");
        state.Begin(name, available / 2, 20, state.Left(name, width));
        Check(state.Hold() && state.CollapsedProvider == null, "next hold expands");
        state.End(true);
        Check(state.Order.SequenceEqual(order), "expand restores reordered slots");
    }

    state.Begin("ChatGPT", 10, 10, state.Left("ChatGPT", width));
    state.Move(10, 35, width);
    Check(!state.Hold() && state.End(true) == null, "vertical motion cancels click and hold");
    state.Begin("ChatGPT", 10, 10, state.Left("ChatGPT", width));
    Check(state.End(false) == null, "release outside cannot click");
    state.Begin("ChatGPT", 10, 10, state.Left("ChatGPT", width));
    state.End(false, cancel: true);
    Check(!state.Hold(), "cancelled timer cannot collapse");
}

Console.WriteLine($"Provider tabs: {checks} checks passed (click, reorder, hold, expand, cancel, responsive geometry).");
