using FlowDeck.Core;

namespace FlowDeck.Tests;

public class CurveEditingTests
{
    private static readonly CurvePoint[] Curve = [new(30, 25), new(50, 50), new(80, 90)];
    [Fact] public void DragCannotCrossNeighborsOrDecreaseOutput()
    {
        var moved = CurveEditing.Move(Curve, 1, 100, -30, 25, false, 0, 100)!;
        Assert.Equal(79.9, moved.Temperature, 5); Assert.Equal(25, moved.Duty);
        moved = CurveEditing.Move(Curve, 1, -10, 120, 25, false, 0, 100)!;
        Assert.Equal(30.1, moved.Temperature, 5); Assert.Equal(90, moved.Duty);
        Assert.Equal(new CurvePoint(50, 50), Curve[1]);
    }
    [Fact] public void EdgesRespectAxisAndConfirmedMinimum()
    {
        Assert.Equal(new CurvePoint(0, 25), CurveEditing.Move(Curve, 0, -99, 0, 25, false, 0, 100));
        Assert.Equal(new CurvePoint(100, 100), CurveEditing.Move(Curve, 2, 200, 200, 25, false, 0, 100));
        Assert.Null(CurveEditing.Move(Curve, 0, 20, 20, null, false, 0, 100));
    }
    [Fact] public void StopPermitsOnlyZeroOrConfirmedRunningRange()
    {
        Assert.Equal(0, CurveEditing.Move(Curve, 0, 30, 5, 25, true, 0, 100)!.Duty);
        Assert.Equal(25, CurveEditing.Move(Curve, 0, 30, 20, 25, true, 0, 100)!.Duty);
        Assert.Equal(25, CurveEditing.Move(Curve, 1, 50, 0, 25, true, 0, 100)!.Duty);
    }
    [Fact] public void InsertPreservesOrderAndRejectsDuplicateTemperature()
    {
        var inserted = CurveEditing.Insert(Curve, 60, 10, 25, false, 0, 100)!.Value;
        Assert.Equal(2, inserted.Index); Assert.Equal(new CurvePoint(60, 50), inserted.Point);
        Assert.Null(CurveEditing.Insert(Curve, 50, 50, 25, false, 0, 100));
        Assert.Equal((0, new CurvePoint(40, 25)), CurveEditing.Insert([], 40, 0, 25, false, 0, 100));
    }
    [Fact] public void InvalidImportedShapesAreNeverSilentlyReordered()
    {
        CurvePoint[][] invalid = [[new(50, 50), new(30, 70)], [new(30, 70), new(50, 50)], [new(double.NaN, 30)], [new(30, -1)], [new(30, 101)]];
        foreach (var points in invalid)
        { Assert.False(CurveEditing.ValidShape(points, 25, false)); Assert.Null(CurveEditing.Move(points, 0, 40, 50, 25, false, 0, 100)); }
        Assert.Null(CurveEditing.Move(Curve, 1, double.PositiveInfinity, 50, 25, false, 0, 100));
        Assert.Null(CurveEditing.Insert(Curve, 40, double.NaN, 25, false, 0, 100));
    }
    [Fact] public void EditedCurveStillPassesControllerValidation()
    {
        var points = Curve.ToArray(); points[1] = CurveEditing.Move(points, 1, 65.4, 75, 25, false, 0, 100)!;
        var profile = new AutoProfile("edited", "edited", Cpu: new("cpu", points, 95, 85), DescentDelaySeconds: 3, DescentPerSecond: 5);
        Assert.Null(FanPolicyEngine.ValidateProfile(profile, new(25, 100, 50)));
    }
}
