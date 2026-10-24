using UnityEngine;
namespace ACunicombat.Mechs
{
    public static class MechCombatRadiometry
    {
        // Relative dB calibration shared with projectile heat: 28 cold, 58 motor-on.
        // Equipment reference range is a calibration point, not a range cutoff.
        public const float ReferenceSignatureDb=28f;
        public static bool TryMeasure(float distance,float signatureDb,float referenceRange,float efficiency,float bearingSigma,out float error)
        {
            error=0;
            if(!float.IsFinite(distance)||distance<0||!float.IsFinite(signatureDb)||referenceRange<=0||efficiency<=0)return false;
            float margin=signatureDb-ReferenceSignatureDb-20*Mathf.Log10(Mathf.Max(1,distance)/referenceRange)+10*Mathf.Log10(Mathf.Clamp01(efficiency));
            if(margin<0)return false;
            error=Mathf.Max(.2f,distance*bearingSigma/Mathf.Sqrt(Mathf.Pow(10,margin/10)));
            return true;
        }
    }
}
