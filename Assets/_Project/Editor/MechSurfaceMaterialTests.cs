using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEditor;
using System.IO;

public class MechSurfaceMaterialTests
{
    [Test] public void RestoredNormalIsLinearOpaqueDataWithMipmaps()
    {
        var n=Resources.Load<Texture2D>("AC6Materials/Normals/BD_M_1010_01_n");
        Assert.IsNotNull(n);Assert.IsFalse(GraphicsFormatUtility.IsSRGBFormat(n.graphicsFormat));
        var importer=(TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(n));
        Assert.IsFalse(importer.sRGBTexture);Assert.IsFalse(importer.alphaIsTransparency);
        Assert.IsTrue(importer.mipmapEnabled);Assert.AreEqual(TextureImporterType.Default,importer.textureType);
    }
    [Test] public void NpcSurfacePreparationRestoresNormalsWithoutChangingPaintOrSourceAsset()
    {
        var root=GameObject.CreatePrimitive(PrimitiveType.Cube);
        var shader=Shader.Find("ACunicombat/AC6/Parts Approx");
        var source=new Material(shader){name="Armor"};var damaged=new Texture2D(2,2){name="BD_M_1010_01_n"};
        source.SetTexture("_BumpMap",damaged);source.SetColor("_BaseColor",Color.red);
        root.GetComponent<Renderer>().sharedMaterial=source;
        try
        {
            MechAppearanceApplicator.PrepareSurfaces(root);
            var actual=root.GetComponent<Renderer>().sharedMaterial;
            Assert.AreNotSame(source,actual);Assert.AreEqual(Color.red,actual.GetColor("_BaseColor"));
            Assert.AreEqual(1,actual.GetFloat("_NormalEncoding"));Assert.AreNotSame(damaged,actual.GetTexture("_BumpMap"));
            Assert.AreSame(damaged,source.GetTexture("_BumpMap"));
            MechAppearanceApplicator.PrepareSurfaces(root);
            Assert.AreSame(actual,root.GetComponent<Renderer>().sharedMaterial,"Repeated preparation must not leak material copies.");
        }
        finally{Object.DestroyImmediate(root);Object.DestroyImmediate(source);Object.DestroyImmediate(damaged);}
    }
    [Test] public void AllShaderPassesShareOneMaterialLayout()
    {
        var s=File.ReadAllText("Assets/_Project/Shaders/AC6PartsApprox.shader");
        var passes=s.Split(new[]{"HLSLPROGRAM"},System.StringSplitOptions.None);
        Assert.AreEqual(5,passes.Length);
        for(int i=1;i<passes.Length;i++)Assert.That(passes[i],Does.Contain("AC6PartsMaterialInput.hlsl"));
        Assert.That(s,Does.Not.Contain("CBUFFER_START(UnityPerMaterial)"));
    }
}
