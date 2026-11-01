using System;
namespace ACunicombat.Mechs
{
    public readonly struct MechMissileBankSnapshot
    {
        public readonly int Index,Rounds,LoadingRounds;
        public readonly string AmmoId,ReloadAmmoId;
        public readonly double ReloadCompleteTime;
        public MechMissileBankSnapshot(int index,MechMissileBanks.Bank bank,string reserveAmmo)
        {Index=index;Rounds=bank.Rounds;LoadingRounds=bank.Loading;AmmoId=bank.Ammo;ReloadAmmoId=reserveAmmo;ReloadCompleteTime=bank.ReloadAt;}
    }
    // Runtime inventory belongs to the mounted equipment instance, not the ammo asset.
    public sealed class MechMissileBanks
    {
        public sealed class Bank { public string Ammo; public int Rounds; public double ReloadAt=double.PositiveInfinity; public int Loading; }
        public readonly MechMissileLauncherDefinition Launcher;
        public readonly Bank[] Banks;
        public int Reserve {get;private set;}
        public string ReserveAmmo {get;}
        public int Loaded {get{int n=0;foreach(var b in Banks)n+=b.Rounds;return n;}}
        public int Loading {get{int n=0;foreach(var b in Banks)n+=b.Loading;return n;}}
        public int Total=>Loaded+Reserve+Loading;
        public int LongestLoadedBank()
        {int best=-1;float maximum=0;for(int i=0;i<Banks.Length;i++){float range=Range(Banks[i].Ammo);if(Banks[i].Rounds>0&&range>maximum){maximum=range;best=i;}}return best;}
        public const double ReloadSeconds=4; // Legacy default; instances use Launcher.ReloadSeconds.
        public bool UsesRangeSelection=>Launcher.Number is "4250" or "4251" or "4252";
        private static readonly System.Collections.Generic.Dictionary<string,float> ranges=new();
        private static float Range(string ammo){if(!ranges.TryGetValue(ammo,out float r)){r=MechMissileAmmunitionCatalog.Create(ammo).maximumRangeMeters;ranges.Add(ammo,r);}return r;}
        private static readonly float StandardRange=MechMissileAmmunitionCatalog.Create("JW7").maximumRangeMeters;
        private static readonly float ErRange=MechMissileAmmunitionCatalog.Create("JW7-ER").maximumRangeMeters;
        public MechMissileBanks(MechMissileLauncherDefinition launcher,string selection)
        {
            Launcher=launcher;Banks=new Bank[launcher.LoadedGroups];
            bool mixed=launcher.Number=="4252"&&selection=="jw7-mixed";
            string ammo=UsesRangeSelection?(selection=="jw7-er"&&launcher.Allows("JW7-ER")?"JW7-ER":"JW7"):(launcher.Allows(selection)?selection:launcher.DefaultAmmo);
            if(launcher.Number=="4252")ammo="JW7";
            for(int i=0;i<Banks.Length;i++)Banks[i]=new Bank{Ammo=mixed&&i==0?"JW7-ER":ammo,Rounds=launcher.GroupCapacity};
            Reserve=(launcher.TotalGroups-launcher.LoadedGroups)*launcher.GroupCapacity;
            ReserveAmmo=ammo;
        }
        public void Tick(double now)
        {
            foreach(var b in Banks)if(b.Loading>0&&now>=b.ReloadAt)
            {b.Rounds=b.Loading;b.Loading=0;b.Ammo=ReserveAmmo;b.ReloadAt=double.PositiveInfinity;}
        }
        // Prefer the shortest-range compatible loaded ammunition; never fabricate
        // an ER round from the ordinary reserve. Geometry is checked by the FCS too.
        public int Select(double targetDistance)
        {
            int best=-1;float bestRange=float.PositiveInfinity;
            for(int i=0;i<Banks.Length;i++)
            {var b=Banks[i];if(b.Rounds<=0)continue;float range=Range(b.Ammo);
                if((!UsesRangeSelection||targetDistance<=range)&&range<bestRange){best=i;bestRange=range;}}
            return best;
        }
        public bool Consume(int bank,double now)
        {
            if(bank<0||bank>=Banks.Length||Banks[bank].Rounds<=0)return false;
            var b=Banks[bank];b.Rounds--;
            if(b.Rounds==0&&Reserve>0)
            {b.Loading=Math.Min(Launcher.GroupCapacity,Reserve);Reserve-=b.Loading;b.ReloadAt=now+Launcher.ReloadSeconds;}
            return true;
        }
    }
}
