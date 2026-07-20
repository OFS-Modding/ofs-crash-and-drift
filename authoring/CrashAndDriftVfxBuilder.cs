using System;
using UnityEditor;
using UnityEngine;

namespace OFS.ModAuthoring.Editor
{
    /// <summary>Generates the script-free visual effect shipped by Crash & Drift.</summary>
    public static class CrashAndDriftVfxBuilder
    {
        private const string Root = "Assets/ModContent/CrashAndDrift";
        private const string BundleName = "ofs-crash-and-drift-vfx";

        public static void GenerateAndBuildFromCommandLine()
        {
            Generate();
            OFSBundleBuilder.BuildFromCommandLine();
        }

        [MenuItem("OFS SDK/Generate Crash and Drift VFX")]
        public static void Generate()
        {
            ClearGeneratedContent();
            if (AssetDatabase.IsValidFolder(Root))
            {
                ValidateAndAssignExistingAssets();
                Debug.Log($"OFS reused the canonical Crash & Drift VFX sources for '{BundleName}'.");
                return;
            }
            EnsureFolder("Assets/ModContent", "CrashAndDrift");

            var fireMaterial = CreateParticleMaterial(
                $"{Root}/CrashFire.mat",
                "Crash Fire",
                new Color(1f, 0.23f, 0.015f, 1f));
            var smokeMaterial = CreateParticleMaterial(
                $"{Root}/CrashSmoke.mat",
                "Crash Smoke",
                new Color(0.13f, 0.13f, 0.13f, 0.82f));

            var root = new GameObject("Crash Explosion VFX");
            try
            {
                ConfigureSmoke(root, smokeMaterial);

                var fire = new GameObject("Fire Burst");
                fire.transform.SetParent(root.transform, false);
                ConfigureFire(fire, fireMaterial);

                var sparks = new GameObject("Sparks");
                sparks.transform.SetParent(root.transform, false);
                ConfigureSparks(sparks, fireMaterial);

                var prefabPath = $"{Root}/CrashExplosion.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath)
                    ?? throw new InvalidOperationException("Unity did not save CrashExplosion.prefab.");
                if (prefab.GetComponentsInChildren<ParticleSystem>(true).Length != 3 ||
                    prefab.GetComponentsInChildren<ParticleSystemRenderer>(true).Length != 3)
                    throw new InvalidOperationException(
                        "CrashExplosion.prefab must contain exactly three particle systems and renderers.");
                AssignBundle(prefabPath);
                AssignBundle(AssetDatabase.GetAssetPath(fireMaterial));
                AssignBundle(AssetDatabase.GetAssetPath(smokeMaterial));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.RemoveUnusedAssetBundleNames();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"OFS generated Crash & Drift VFX bundle '{BundleName}'.");
        }

        private static void ConfigureSmoke(GameObject target, Material material)
        {
            var system = target.AddComponent<ParticleSystem>();
            system.useAutoRandomSeed = false;
            system.randomSeed = 0xC4A51001u;
            var main = system.main;
            main.loop = false;
            main.duration = 0.25f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.15f, 2.1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.2f, 5.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.4f, 3.1f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.08f, 0.08f, 0.08f, 0.88f),
                new Color(0.32f, 0.32f, 0.32f, 0.68f));
            main.maxParticles = 64;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction = ParticleSystemStopAction.Destroy;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 34) });

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.65f;

            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = FadeGradient(
                new Color(0.42f, 0.42f, 0.42f, 0.78f),
                new Color(0.04f, 0.04f, 0.04f, 0f));

            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.45f, 1f, 1.45f));
            target.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
        }

        private static void ConfigureFire(GameObject target, Material material)
        {
            var system = target.AddComponent<ParticleSystem>();
            system.useAutoRandomSeed = false;
            system.randomSeed = 0xC4A51002u;
            var main = system.main;
            main.loop = false;
            main.duration = 0.1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.72f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(8f, 17f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.45f, 1.45f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.9f, 0.12f, 1f),
                new Color(1f, 0.08f, 0.005f, 1f));
            main.maxParticles = 120;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 78) });

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.48f;

            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = FadeGradient(
                new Color(1f, 0.88f, 0.1f, 1f),
                new Color(0.9f, 0.015f, 0f, 0f));

            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.08f));
            target.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
        }

        private static void ConfigureSparks(GameObject target, Material material)
        {
            var system = target.AddComponent<ParticleSystem>();
            system.useAutoRandomSeed = false;
            system.randomSeed = 0xC4A51003u;
            var main = system.main;
            main.loop = false;
            main.duration = 0.1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.85f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(11f, 24f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.24f);
            main.startColor = new ParticleSystem.MinMaxGradient(Color.yellow, new Color(1f, 0.22f, 0f, 1f));
            main.gravityModifier = 1.4f;
            main.maxParticles = 90;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 54) });

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.3f;

            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = FadeGradient(Color.white, new Color(1f, 0.04f, 0f, 0f));
            target.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
        }

        private static ParticleSystem.MinMaxGradient FadeGradient(Color start, Color end)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) },
                new[] { new GradientAlphaKey(start.a, 0f), new GradientAlphaKey(end.a, 1f) });
            return new ParticleSystem.MinMaxGradient(gradient);
        }

        private static Material CreateParticleMaterial(string path, string name, Color color)
        {
            var shader = Shader.Find("HDRP/Unlit")
                ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Standard")
                ?? throw new InvalidOperationException("No compatible particle shader was found.");
            var material = new Material(shader) { name = name, color = color };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void ClearGeneratedContent()
        {
            if (AssetDatabase.IsValidFolder("Assets/ModContent/Fixture") &&
                !AssetDatabase.DeleteAsset("Assets/ModContent/Fixture"))
                throw new InvalidOperationException("Could not clear the verification fixture.");
        }

        private static void ValidateAndAssignExistingAssets()
        {
            var prefabPath = $"{Root}/CrashExplosion.prefab";
            var firePath = $"{Root}/CrashFire.mat";
            var smokePath = $"{Root}/CrashSmoke.mat";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)
                ?? throw new InvalidOperationException($"Canonical prefab '{prefabPath}' is missing.");
            if (AssetDatabase.LoadAssetAtPath<Material>(firePath) == null ||
                AssetDatabase.LoadAssetAtPath<Material>(smokePath) == null)
                throw new InvalidOperationException("Canonical Crash & Drift particle materials are missing.");
            if (prefab.GetComponentsInChildren<ParticleSystem>(true).Length != 3 ||
                prefab.GetComponentsInChildren<ParticleSystemRenderer>(true).Length != 3)
                throw new InvalidOperationException(
                    "Canonical CrashExplosion.prefab must contain exactly three particle systems and renderers.");
            AssignBundle(prefabPath);
            AssignBundle(firePath);
            AssignBundle(smokePath);
            AssetDatabase.RemoveUnusedAssetBundleNames();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static void EnsureFolder(string parent, string name)
        {
            var path = $"{parent}/{name}";
            if (!AssetDatabase.IsValidFolder(path) &&
                string.IsNullOrWhiteSpace(AssetDatabase.CreateFolder(parent, name)))
                throw new InvalidOperationException($"Could not create '{path}'.");
        }

        private static void AssignBundle(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath)
                ?? throw new InvalidOperationException($"No importer exists for '{assetPath}'.");
            importer.assetBundleName = BundleName;
            importer.assetBundleVariant = string.Empty;
            importer.SaveAndReimport();
        }
    }
}
