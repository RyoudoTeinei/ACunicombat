using System;
using System.IO;
using ACunicombat.Mechs;
public static class MechTacticalRangeReview
{
 public static void Run()
 {
  var cases=new[]{(false,250000f,100000f),(true,99000f,100000f),(true,100000f,100000f),(true,120000f,132000f),(true,120100f,132110f),(false,120000f,100000f),(true,float.NaN,100000f)};
  string result="";
  foreach(var item in cases){float actual=MechRadarHudPresenter.ResolveTacticalDisplayRange(item.Item1,item.Item2);if(Math.Abs(actual-item.Item3)>.1f)throw new Exception("Tactical range rule failed");result+=$"lock={item.Item1}; target={item.Item2}; displayed={actual}\n";}
  string folder=Environment.GetEnvironmentVariable("ACUNICOMBAT_LIVE_REVIEW");Directory.CreateDirectory(folder);File.WriteAllText(folder+"/range-checks.txt",result+"PASS");
  MechLiveBattlefieldReview.RunUiScreensReview();
 }
}
