using OneBox.Plugins.Screenshot.Core;
namespace OneBox.Tests;
public class ScrollInputPolicyTests
{
    [Theory]
    [InlineData(3482,644,true,true)]
    [InlineData(3000,400,true,true)]
    [InlineData(74,41,true,false)]
    [InlineData(4474,600,true,false)]
    [InlineData(3524,666,false,false)]
    public void ActualUserRegionAllowsMovementButNotOutsideOrScrollbar(int x,int y,bool released,bool expected)
        => Assert.Equal(expected,ScrollInputPolicy.IsInsideCapture(2573,208,1902,917,x,y,released));

    [Theory]
    [InlineData(0,0,true,true)]
    [InlineData(1,-2,true,true)]
    [InlineData(12,-12,true,true)]
    [InlineData(13,0,true,false)]
    [InlineData(0,13,true,false)]
    [InlineData(0,0,false,false)]
    public void PointerJitterAndUserTakeoverAreDistinct(int dx,int dy,bool released,bool expected)
        => Assert.Equal(expected,ScrollInputPolicy.CanUsePointer(3500,660,3500+dx,660+dy,released));
    [Fact]
    public void CoordinatesDoNotOverflow()
        => Assert.False(ScrollInputPolicy.CanUsePointer(int.MinValue,0,int.MaxValue,0,true));
}
