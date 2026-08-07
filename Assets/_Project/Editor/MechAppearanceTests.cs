using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

public sealed class MechAppearanceTests
{
    [Test]
    public void NormalizeClampsMaterialAndWeatheringControls()
    {
        var settings = new MechAppearanceSettings
        {
            reflectivenessLevel = -3,
            lusterLevel = 20,
            weatheringAmount = 1.8f,
            baseColor = new Color(-1f, 2f, 0.5f, 0.2f)
        };

        settings.Normalize();

        Assert.That(settings.reflectivenessLevel, Is.EqualTo(0));
        Assert.That(settings.lusterLevel, Is.EqualTo(5));
        Assert.That(settings.weatheringAmount, Is.EqualTo(1f));
        Assert.That(settings.baseColor, Is.EqualTo(new Color(0f, 1f, 0.5f, 1f)));
    }

    [Test]
    public void ApplicatorWritesFivePaintRegionsAndMaterialResponse()
    {
        var shader = Shader.Find("ACunicombat/AC6/Parts Approx");
        Assert.That(shader, Is.Not.Null);

        var gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var material = new Material(shader);
        try
        {
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            var settings = new MechAppearanceSettings
            {
                baseColor = Color.red,
                subColor = Color.green,
                supportColor = Color.blue,
                optionalColor = Color.yellow,
                deviceColor = Color.cyan,
                reflectivenessLevel = 5,
                lusterLevel = 0,
                weatheringAmount = 0.73f
            };

            MechAppearanceApplicator.Apply(gameObject, settings);

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, 0);
            Assert.That(block.GetColor("_Zone1Color"), Is.EqualTo(Color.red));
            Assert.That(block.GetColor("_Zone2Color"), Is.EqualTo(Color.green));
            Assert.That(block.GetColor("_Zone3Color"), Is.EqualTo(Color.blue));
            Assert.That(block.GetColor("_Zone4Color"), Is.EqualTo(Color.yellow));
            Assert.That(block.GetColor("_Zone5Color"), Is.EqualTo(Color.cyan));
            Assert.That(block.GetFloat("_WeatheringAmount"), Is.EqualTo(0.73f).Within(0.0001f));
            Assert.That(block.GetFloat("_Zone1Metallic"), Is.EqualTo(0.12f).Within(0.0001f));
            Assert.That(block.GetFloat("_Zone5Metallic"), Is.GreaterThan(block.GetFloat("_Zone1Metallic")));
            Assert.That(block.GetFloat("_Zone1Smoothness"), Is.EqualTo(0.22f).Within(0.0001f));
        }
        finally
        {
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ApplicatorConvertsGenericEquipmentMaterialToPlayerAppearanceShader()
    {
        var genericShader = Shader.Find("Universal Render Pipeline/Lit");
        var appearanceShader = Shader.Find("ACunicombat/AC6/Parts Approx");
        Assert.That(genericShader, Is.Not.Null);
        Assert.That(appearanceShader, Is.Not.Null);

        var gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var sourceMaterial = new Material(genericShader) { name = "ImportedEquipment" };
        var sourceTexture = new Texture2D(2, 2);
        try
        {
            sourceTexture.SetPixels(new[] { Color.white, Color.black, Color.white, Color.black });
            sourceTexture.Apply();
            sourceMaterial.SetTexture("_BaseMap", sourceTexture);
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = sourceMaterial;
            var settings = new MechAppearanceSettings
            {
                baseColor = new Color(0.12f, 0.34f, 0.56f),
                reflectivenessLevel = 4,
                lusterLevel = 5,
                weatheringAmount = 0.31f
            };

            MechAppearanceApplicator.Apply(gameObject, settings);

            var converted = renderer.sharedMaterial;
            Assert.That(converted, Is.Not.SameAs(sourceMaterial));
            Assert.That(converted.shader, Is.SameAs(appearanceShader));
            Assert.That(converted.GetTexture("_BaseMap"), Is.SameAs(sourceTexture));
            Assert.That(converted.GetFloat("_UseSourceAlbedo"), Is.EqualTo(1f));
            Assert.That(converted.GetFloat("_ZoneMode"), Is.EqualTo(0f));

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, 0);
            Assert.That(block.GetColor("_BaseColor"), Is.EqualTo(settings.baseColor));
            Assert.That(block.GetFloat("_WeatheringAmount"), Is.EqualTo(0.31f).Within(0.0001f));
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
            Object.DestroyImmediate(sourceMaterial);
            Object.DestroyImmediate(sourceTexture);
        }
    }

    [Test]
    public void ApplicatorPreservesImportedEyeMaterial()
    {
        var genericShader = Shader.Find("Universal Render Pipeline/Lit");
        Assert.That(genericShader, Is.Not.Null);

        var gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var eyeMaterial = new Material(genericShader) { name = "WP_4251_01_Wep_EYE" };
        try
        {
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = eyeMaterial;

            MechAppearanceApplicator.Apply(gameObject, new MechAppearanceSettings());

            Assert.That(renderer.sharedMaterial, Is.SameAs(eyeMaterial));
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
            Object.DestroyImmediate(eyeMaterial);
        }
    }

    [Test]
    public void ApplicatorHidesEmptyAc6MarkingSlots()
    {
        var sourceShader = Shader.Find("Universal Render Pipeline/Lit");
        Assert.That(sourceShader, Is.Not.Null);

        var gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var markingMaterial = new Material(sourceShader) { name = "Prats_Making_mat" };
        try
        {
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = markingMaterial;

            MechAppearanceApplicator.Apply(gameObject, new MechAppearanceSettings());

            var hiddenMaterial = renderer.sharedMaterial;
            Assert.That(hiddenMaterial, Is.Not.SameAs(markingMaterial));
            Assert.That(hiddenMaterial.name, Is.EqualTo("AC6_HiddenOverlaySlot"));
            Assert.That(hiddenMaterial.renderQueue, Is.EqualTo((int)UnityEngine.Rendering.RenderQueue.Transparent));
            Assert.That(hiddenMaterial.GetColor("_BaseColor").a, Is.EqualTo(0f));
            Assert.That(hiddenMaterial.GetShaderPassEnabled("DepthOnly"), Is.False);
            Assert.That(hiddenMaterial.GetShaderPassEnabled("ShadowCaster"), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
            Object.DestroyImmediate(markingMaterial);
        }
    }

    [Test]
    public void ApplicatorCopiesGltfFastBaseAndNormalTextures()
    {
        var gltfShader = Shader.Find("Shader Graphs/glTF-pbrMetallicRoughness");
        var appearanceShader = Shader.Find("ACunicombat/AC6/Parts Approx");
        Assert.That(gltfShader, Is.Not.Null);
        Assert.That(appearanceShader, Is.Not.Null);

        var gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var sourceMaterial = new Material(gltfShader) { name = "WP_1320_01" };
        var baseTexture = new Texture2D(2, 2);
        var normalTexture = new Texture2D(2, 2);
        try
        {
            sourceMaterial.SetTexture("baseColorTexture", baseTexture);
            sourceMaterial.SetTexture("normalTexture", normalTexture);
            sourceMaterial.SetFloat("normalTexture_scale", 0.63f);
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = sourceMaterial;

            MechAppearanceApplicator.Apply(gameObject, new MechAppearanceSettings());

            var converted = renderer.sharedMaterial;
            Assert.That(converted, Is.Not.SameAs(sourceMaterial));
            Assert.That(converted.shader, Is.SameAs(appearanceShader));
            Assert.That(converted.GetTexture("_BaseMap"), Is.SameAs(baseTexture));
            Assert.That(converted.GetTexture("_BumpMap"), Is.SameAs(normalTexture));
            Assert.That(converted.GetFloat("_BumpScale"), Is.EqualTo(0.63f).Within(0.0001f));
            Assert.That(converted.GetFloat("_UseSourceAlbedo"), Is.EqualTo(1f));
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
            Object.DestroyImmediate(sourceMaterial);
            Object.DestroyImmediate(baseTexture);
            Object.DestroyImmediate(normalTexture);
        }
    }
}
