using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.CrashAndDriftMod;

public sealed class CrashAndDriftMod : IOFSMod
{
    private const string GameAssembly = "Assembly-CSharp.dll";
    private const string CoreAssembly = "UnityEngine.CoreModule.dll";
    private const string PhysicsAssembly = "UnityEngine.PhysicsModule.dll";
    private const float SidewaysGripMultiplier = 0.22f;
    private const float MinimumGripMultiplier = 0.2f;
    private const float DriftYawMultiplier = 1.45f;

    private IModContext? _context;
    private IUnsafeIl2CppApi? _api;
    private IIl2CppMethodDetour<HandleCollisionDelegate>? _collisionHook;
    private HandleCollisionDelegate? _originalCollision;
    private IModInputAction? _leftShift;
    private IModInputAction? _rightShift;
    private IModMesh? _explosionMesh;
    private IModMaterial? _flashMaterial;
    private IModMaterial? _smokeMaterial;
    private UnityObject _resourceAnchor;
    private readonly List<IDisposable> _resourceAnchorBindings = [];
    private readonly List<ExplosionEffect> _explosionEffects = [];
    private bool _leftHeld;
    private bool _rightHeld;
    private bool _unloading;
    private nint _activeDrivetrain;
    private DriftDefaults _activeDefaults;
    private bool _driftApplied;
    private bool _driftFaulted;
    private bool _driftProbeLogged;
    private readonly HashSet<nint> _pendingTargets = [];

    private nint _sccNetworkClass;
    private nint _sccDrivetrainClass;
    private nint _sccInputProcessorClass;
    private nint _vehicleComponentClass;
    private string? _vehicleComponentAssembly;
    private nint _netField;
    private nint _drivetrainField;
    private nint _inputProcessorField;
    private nint _inputActiveField;
    private nint _defaultSidewaysStiffnessField;
    private nint _gripBreakMinFrictionField;
    private nint _driftYawTorqueField;
    private nint _isLocalOccupant;
    private nint _collisionGetGameObject;
    private nint _componentGetGameObject;
    private nint _transformGetParent;
    private nint _deactivateTrafficVehicle;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HandleCollisionDelegate(nint instance, nint collision, nint methodInfo);

    public void Load(IModContext context)
    {
        _context = context;
        _api = context.UnsafeIl2Cpp;
        RequireSupportedBuild(context);
        ResolveMetadata();

        _collisionHook = context.Hooks.InstallIl2Cpp(
            new Il2CppMethodDetourDefinition(
                "scc-audio-handle-collision",
                GameAssembly,
                string.Empty,
                "SCC_Audio",
                "HandleCollision",
                1),
            new HandleCollisionDelegate(OnVehicleCollision));
        _originalCollision = _collisionHook.OriginalDelegate;

        if (!context.Input.IsAvailable)
            throw new NotSupportedException("Crash & Drift requires Unity Input System.");
        _leftShift = RegisterShift(ModKey.LeftShift, "drift-left-shift");
        _rightShift = RegisterShift(ModKey.RightShift, "drift-right-shift");

        context.Events.SceneUnloaded += OnSceneUnloaded;
        context.Events.SceneLoaded += OnSceneLoaded;
        context.Events.FrameUpdate += OnFrame;
        context.Log.Info(
            "Crash & Drift loaded: collision destruction is visual-only and Shift drift assist is ready; " +
            $"traffic={_vehicleComponentAssembly ?? "unavailable"}.");
    }

    public void Unload()
    {
        _unloading = true;
        RestoreDriftIfAlive();
        if (_context is not null)
        {
            _context.Events.SceneUnloaded -= OnSceneUnloaded;
            _context.Events.SceneLoaded -= OnSceneLoaded;
            _context.Events.FrameUpdate -= OnFrame;
        }
        _leftShift?.Dispose();
        _rightShift?.Dispose();
        _collisionHook?.Dispose();
        ReleaseExplosionVisuals();
        _pendingTargets.Clear();
    }

    private IModInputAction RegisterShift(ModKey key, string id) =>
        _context!.Input.Register(new ModInputActionDefinition(
            id,
            InputBinding.ForKey(key),
            input =>
            {
                var held = (input.Trigger & InputTrigger.Held) != 0;
                if (key == ModKey.LeftShift) _leftHeld = held;
                else _rightHeld = held;
            },
            Trigger: InputTrigger.Held | InputTrigger.Released,
            CapturePolicy: InputCapturePolicy.Always));

    private void OnSceneUnloaded(SceneEvent scene)
    {
        if (!string.Equals(scene.Name, "Factory", StringComparison.OrdinalIgnoreCase)) return;
        RestoreDriftIfAlive();
        _pendingTargets.Clear();
        ReleaseExplosionVisuals();
    }

    private void OnSceneLoaded(SceneEvent scene)
    {
        if (string.Equals(scene.Name, "Factory", StringComparison.OrdinalIgnoreCase))
            PrepareExplosionVisuals();
    }

    private void OnFrame(FrameEvent _)
    {
        if (_unloading || _context is null) return;
        UpdateExplosionEffects(_.DeltaTime);
        try
        {
            if ((_leftHeld || _rightHeld) && !_driftFaulted) ApplyDriftToLocalDriver();
            else RestoreDriftIfAlive();
        }
        catch (Exception exception)
        {
            _driftFaulted = true;
            _leftHeld = false;
            _rightHeld = false;
            RestoreDriftIfAlive();
            _context.Log.Error(
                exception,
                "Shift drift assist disabled for this session after a safe IL2CPP access failed.");
        }
    }

    private void OnVehicleCollision(nint instance, nint collision, nint methodInfo)
    {
        try
        {
            _originalCollision?.Invoke(instance, collision, methodInfo);
        }
        catch (Exception exception)
        {
            _context?.Log.Error(exception, "Vanilla SCC collision handler failed.");
        }

        try
        {
            if (_unloading || instance == 0 || collision == 0 || !IsLocallyDrivenAudio(instance)) return;

            var attackerObject = GetComponentGameObject(instance);
            var attacker = FindVehicleRoot(attackerObject);
            var otherObject = _api!.Invoke(_collisionGetGameObject, collision);
            var target = FindVehicleRoot(otherObject);
            if (target.Kind != VehicleKind.Traffic || target.GameObject == 0 ||
                target.GameObject == attacker.GameObject ||
                !_pendingTargets.Add(target.GameObject)) return;

            _context!.MainThread.Post(() => ExplodeAndRemove(target));
        }
        catch (Exception exception)
        {
            // No managed exception may cross the unmanaged IL2CPP detour boundary.
            _context?.Log.Error(exception, "Crash & Drift ignored a collision it could not classify safely.");
        }
    }

    private void ExplodeAndRemove(VehicleTarget target)
    {
        _pendingTargets.Remove(target.GameObject);
        if (_unloading || _context is null || !IsAliveGameObject(target.GameObject)) return;

        UnityVector3 position;
        try
        {
            position = _context.Unity.GetTransform(new UnityObject(target.GameObject)).Position;

            _ = _api!.Invoke(_deactivateTrafficVehicle, target.Component);

            _context.Log.Info(
                $"Collision removed {target.Kind} vehicle at " +
                $"({position.X:0.0}, {position.Y:0.0}, {position.Z:0.0}); damage=False.");
        }
        catch (Exception exception)
        {
            _context.Log.Error(exception, "Crash & Drift could not remove the collided vehicle.");
            return;
        }

        try
        {
            SpawnExplosionEffect(position, "Flash", _flashMaterial!, 0.45f, 4.5f, 0.55f);
            SpawnExplosionEffect(
                new UnityVector3(position.X, position.Y + 0.8f, position.Z),
                "Smoke",
                _smokeMaterial!,
                0.8f,
                3.2f,
                1.1f);
            _context.Log.Info("Traffic explosion VFX spawned successfully.");
        }
        catch (Exception exception)
        {
            _context.Log.Error(
                exception,
                "Vehicle was removed, but its visual explosion could not be spawned.");
        }
    }

    private void PrepareExplosionVisuals()
    {
        if (_explosionMesh?.IsLoaded == true &&
            _flashMaterial?.IsLoaded == true &&
            _smokeMaterial?.IsLoaded == true) return;
        _explosionMesh = _context!.Assets.CreateMesh(
            "Crash Explosion Octahedron",
            OctahedronGeometry(),
            uploadMeshData: true);
        _flashMaterial = CreateExplosionMaterial(
            "Crash Explosion Flash", new UnityColor(1f, 0.22f, 0.015f, 1f));
        _smokeMaterial = CreateExplosionMaterial(
            "Crash Explosion Smoke", new UnityColor(0.12f, 0.12f, 0.12f, 1f));
        CreateResourceAnchor();
    }

    private void CreateResourceAnchor()
    {
        _resourceAnchor = _context!.Unity.CreateGameObject("Crash & Drift VFX Resources");
        _context.Unity.DontDestroyOnLoad(_resourceAnchor);
        AnchorMaterial("Flash", _flashMaterial!);
        AnchorMaterial("Smoke", _smokeMaterial!);
        _context.Unity.SetActive(_resourceAnchor, false);
    }

    private void AnchorMaterial(string name, IModMaterial material)
    {
        var child = _context!.Unity.CreateGameObject(name, _resourceAnchor);
        var filter = _context.Unity.AddComponent(
            child, CoreAssembly, "UnityEngine", "MeshFilter");
        var renderer = _context.Unity.AddComponent(
            child, CoreAssembly, "UnityEngine", "MeshRenderer");
        _resourceAnchorBindings.Add(_context.Assets.BindMeshFilter(filter, _explosionMesh!));
        _resourceAnchorBindings.Add(_context.Assets.BindRendererMaterial(renderer, 0, material));
    }

    private IModMaterial CreateExplosionMaterial(string name, UnityColor color)
    {
        var material = _context!.Assets.CreateMaterial("Universal Render Pipeline/Unlit", name);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        return material;
    }

    private void SpawnExplosionEffect(
        UnityVector3 position,
        string suffix,
        IModMaterial material,
        float startScale,
        float maxScale,
        float duration)
    {
        var gameObject = _context!.Unity.CreateGameObject($"Crash & Drift {suffix}");
        _context.Unity.SetTransform(
            gameObject,
            new UnityTransform(
                position,
                UnityQuaternion.Identity,
                new UnityVector3(startScale, startScale, startScale)));
        var filter = _context.Unity.AddComponent(
            gameObject, CoreAssembly, "UnityEngine", "MeshFilter");
        var renderer = _context.Unity.AddComponent(
            gameObject, CoreAssembly, "UnityEngine", "MeshRenderer");
        var meshBinding = _context.Assets.BindMeshFilter(filter, _explosionMesh!);
        var materialBinding = _context.Assets.BindRendererMaterial(renderer, 0, material);
        _explosionEffects.Add(new ExplosionEffect(
            gameObject,
            meshBinding,
            materialBinding,
            position,
            startScale,
            maxScale,
            duration));
    }

    private void UpdateExplosionEffects(float deltaTime)
    {
        for (var index = _explosionEffects.Count - 1; index >= 0; --index)
        {
            var effect = _explosionEffects[index];
            effect.Age += deltaTime;
            if (effect.Age >= effect.Duration)
            {
                DisposeExplosionEffect(effect);
                _explosionEffects.RemoveAt(index);
                continue;
            }
            var progress = effect.Age / effect.Duration;
            var pulse = MathF.Sin(progress * MathF.PI);
            var scale = effect.StartScale + (effect.MaxScale - effect.StartScale) * pulse;
            _context!.Unity.SetTransform(
                effect.GameObject,
                new UnityTransform(
                    effect.Position,
                    UnityQuaternion.Identity,
                    new UnityVector3(scale, scale, scale)));
        }
    }

    private void ClearExplosionEffects()
    {
        foreach (var effect in _explosionEffects.ToArray()) DisposeExplosionEffect(effect);
        _explosionEffects.Clear();
    }

    private void ReleaseExplosionVisuals()
    {
        ClearExplosionEffects();
        for (var index = _resourceAnchorBindings.Count - 1; index >= 0; --index)
            _resourceAnchorBindings[index].Dispose();
        _resourceAnchorBindings.Clear();
        if (_context is not null && !_resourceAnchor.IsNull)
            _context.Unity.Destroy(_resourceAnchor);
        _resourceAnchor = default;
        _explosionMesh?.Dispose();
        _flashMaterial?.Dispose();
        _smokeMaterial?.Dispose();
        _explosionMesh = null;
        _flashMaterial = null;
        _smokeMaterial = null;
    }

    private void DisposeExplosionEffect(ExplosionEffect effect)
    {
        effect.MaterialBinding.Dispose();
        effect.MeshBinding.Dispose();
        if (_context is not null) _context.Unity.Destroy(effect.GameObject);
    }

    private static ModMeshGeometry OctahedronGeometry()
    {
        UnityVector3[] vertices =
        [
            new(0f, 1f, 0f), new(1f, 0f, 0f), new(0f, 0f, 1f),
            new(-1f, 0f, 0f), new(0f, 0f, -1f), new(0f, -1f, 0f)
        ];
        int[] indices =
        [
            0,2,1, 0,3,2, 0,4,3, 0,1,4,
            5,1,2, 5,2,3, 5,3,4, 5,4,1
        ];
        return new ModMeshGeometry(vertices, [new ModSubMeshDefinition(indices)]);
    }

    private void ApplyDriftToLocalDriver()
    {
        if (!_driftProbeLogged)
        {
            _driftProbeLogged = true;
            _context!.Log.Info("Shift drift probe entered; locating the active local drivetrain.");
        }
        var drivetrain = FindLocalDriverDrivetrain();
        if (drivetrain == 0)
        {
            RestoreDriftIfAlive();
            return;
        }
        if (_activeDrivetrain != drivetrain)
        {
            RestoreDriftIfAlive();
            _activeDrivetrain = drivetrain;
            _activeDefaults = new DriftDefaults(
                ReadSingleField(drivetrain, _defaultSidewaysStiffnessField),
                ReadSingleField(drivetrain, _gripBreakMinFrictionField),
                ReadSingleField(drivetrain, _driftYawTorqueField));
            _context!.Log.Info(
                $"Shift drift target acquired: drivetrain=0x{drivetrain:X}, " +
                $"sideways={_activeDefaults.SidewaysStiffness:0.###}, " +
                $"minimumGrip={_activeDefaults.MinimumGrip:0.###}, " +
                $"yaw={_activeDefaults.DriftYawTorque:0.###}.");
            _driftApplied = true;
        }

        WriteSingleField(
            drivetrain,
            _defaultSidewaysStiffnessField,
            MathF.Max(0.01f, _activeDefaults.SidewaysStiffness * SidewaysGripMultiplier));
        WriteSingleField(
            drivetrain,
            _gripBreakMinFrictionField,
            MathF.Max(0.01f, _activeDefaults.MinimumGrip * MinimumGripMultiplier));
        WriteSingleField(
            drivetrain,
            _driftYawTorqueField,
            _activeDefaults.DriftYawTorque * DriftYawMultiplier);
    }

    private void RestoreDriftIfAlive()
    {
        if (!_driftApplied || _activeDrivetrain == 0 || _context is null || _api is null)
        {
            _activeDrivetrain = 0;
            _driftApplied = false;
            return;
        }

        try
        {
            var alive = _context.Unity.FindComponents(
                GameAssembly,
                string.Empty,
                "SCC_Drivetrain",
                activeOnly: true).Any(component => component.Pointer == _activeDrivetrain);
            if (alive)
            {
                WriteSingleField(_activeDrivetrain, _defaultSidewaysStiffnessField, _activeDefaults.SidewaysStiffness);
                WriteSingleField(_activeDrivetrain, _gripBreakMinFrictionField, _activeDefaults.MinimumGrip);
                WriteSingleField(_activeDrivetrain, _driftYawTorqueField, _activeDefaults.DriftYawTorque);
            }
        }
        catch (Exception exception)
        {
            _context.Log.Warning($"Could not restore drift values after the vehicle vanished: {exception.Message}");
        }
        finally
        {
            _activeDrivetrain = 0;
            _driftApplied = false;
        }
    }

    private nint FindLocalDriverDrivetrain()
    {
        foreach (var network in _context!.Unity.FindComponents(
                     GameAssembly,
                     string.Empty,
                     "SCC_Network",
                     activeOnly: true))
        {
            if (!IsCurrentLocalOccupant(network.Pointer)) continue;
            var drivetrain = ReadReferenceField(network.Pointer, _drivetrainField);
            if (drivetrain == 0) continue;
            var processor = ReadReferenceField(drivetrain, _inputProcessorField);
            if (processor != 0 && ReadBooleanField(processor, _inputActiveField)) return drivetrain;
        }
        return 0;
    }

    private bool IsLocallyDrivenAudio(nint audio)
    {
        var network = ReadReferenceField(audio, _netField);
        if (network == 0 || !IsCurrentLocalOccupant(network)) return false;
        var drivetrain = ReadReferenceField(network, _drivetrainField);
        if (drivetrain == 0) return false;
        var processor = ReadReferenceField(drivetrain, _inputProcessorField);
        return processor != 0 && ReadBooleanField(processor, _inputActiveField);
    }

    private VehicleTarget FindVehicleRoot(nint gameObject)
    {
        var current = gameObject;
        for (var depth = 0; current != 0 && depth < 16; ++depth)
        {
            var unityObject = new UnityObject(current);
            var scc = _context!.Unity.TryGetComponent(unityObject, GameAssembly, string.Empty, "SCC_Network");
            if (!scc.IsNull) return new VehicleTarget(current, VehicleKind.Scc);

            if (_vehicleComponentClass != 0)
            {
                var traffic = _context.Unity.TryGetComponent(
                    unityObject,
                    _vehicleComponentAssembly!,
                    "Gley.TrafficSystem",
                    "VehicleComponent");
                if (!traffic.IsNull)
                    return new VehicleTarget(current, traffic.Pointer, VehicleKind.Traffic);
            }

            var transform = _context.Unity.TryGetComponent(
                unityObject,
                CoreAssembly,
                "UnityEngine",
                "Transform");
            if (transform.IsNull) break;
            var parent = _api!.Invoke(_transformGetParent, transform.Pointer);
            current = parent == 0 ? 0 : _api.Invoke(_componentGetGameObject, parent);
        }
        return default;
    }

    private nint GetComponentGameObject(nint component) =>
        _api!.Invoke(_componentGetGameObject, component);

    private bool IsAliveGameObject(nint gameObject)
    {
        try
        {
            _ = _context!.Unity.GetName(new UnityObject(gameObject));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private unsafe nint ReadReferenceField(nint instance, nint field)
    {
        nint value = 0;
        _api!.GetFieldValue(instance, field, (nint)(&value));
        return value;
    }

    private unsafe float ReadSingleField(nint instance, nint field)
    {
        float value = 0;
        _api!.GetFieldValue(instance, field, (nint)(&value));
        return value;
    }

    private unsafe void WriteSingleField(nint instance, nint field, float value) =>
        _api!.SetFieldValue(instance, field, (nint)(&value));

    private unsafe bool ReadBooleanField(nint instance, nint field)
    {
        byte value = 0;
        _api!.GetFieldValue(instance, field, (nint)(&value));
        return value != 0;
    }

    private bool IsCurrentLocalOccupant(nint network)
    {
        var boxed = _api!.Invoke(_isLocalOccupant, network);
        if (boxed == 0) return false;
        var value = _api.Unbox(boxed);
        return value != 0 && Marshal.ReadByte(value) != 0;
    }

    private void ResolveMetadata()
    {
        _sccNetworkClass = RequireClass(GameAssembly, string.Empty, "SCC_Network");
        _sccDrivetrainClass = RequireClass(GameAssembly, string.Empty, "SCC_Drivetrain");
        _sccInputProcessorClass = RequireClass(GameAssembly, string.Empty, "SCC_InputProcessor");
        (_vehicleComponentAssembly, _vehicleComponentClass) = FindOptionalClass(
            "Gley.TrafficSystem",
            "VehicleComponent",
            GameAssembly,
            "GleyTrafficSystem.dll",
            "Gley.TrafficSystem.dll");
        var audioClass = RequireClass(GameAssembly, string.Empty, "SCC_Audio");
        var collisionClass = RequireClass(PhysicsAssembly, "UnityEngine", "Collision");
        var componentClass = RequireClass(CoreAssembly, "UnityEngine", "Component");
        var transformClass = RequireClass(CoreAssembly, "UnityEngine", "Transform");

        _netField = RequireField(audioClass, "net");
        _drivetrainField = RequireField(_sccNetworkClass, "drivetrain");
        _inputProcessorField = RequireField(_sccDrivetrainClass, "inputProcessor");
        _inputActiveField = RequireField(_sccInputProcessorClass, "inputActive");
        _defaultSidewaysStiffnessField = RequireField(_sccDrivetrainClass, "defaultSidewaysStiffness");
        _gripBreakMinFrictionField = RequireField(_sccDrivetrainClass, "gripBreakMinFriction");
        _driftYawTorqueField = RequireField(_sccDrivetrainClass, "driftYawTorque");
        RequireFieldType(_sccNetworkClass, _drivetrainField, "SCC_Drivetrain");
        RequireFieldType(_sccDrivetrainClass, _inputProcessorField, "SCC_InputProcessor");
        RequireFieldType(_sccInputProcessorClass, _inputActiveField, "System.Boolean");
        RequireFieldType(_sccDrivetrainClass, _defaultSidewaysStiffnessField, "System.Single");
        RequireFieldType(_sccDrivetrainClass, _gripBreakMinFrictionField, "System.Single");
        RequireFieldType(_sccDrivetrainClass, _driftYawTorqueField, "System.Single");
        _isLocalOccupant = RequireMethod(_sccNetworkClass, "IsLocalOccupant", 0);
        _collisionGetGameObject = RequireMethod(collisionClass, "get_gameObject", 0);
        _componentGetGameObject = RequireMethod(componentClass, "get_gameObject", 0);
        _transformGetParent = RequireMethod(transformClass, "get_parent", 0);
        if (_vehicleComponentClass == 0)
            throw new TypeLoadException("Gley.TrafficSystem.VehicleComponent is required.");
        _deactivateTrafficVehicle = RequireMethod(
            _vehicleComponentClass, "DeactivateVehicle", 0);
    }

    private nint RequireClass(string assembly, string namespaze, string name)
    {
        var value = _api!.FindClass(assembly, namespaze, name);
        return value != 0 ? value : throw new TypeLoadException($"{assembly}:{namespaze}.{name} was not found.");
    }

    private (string? Assembly, nint Class) FindOptionalClass(
        string namespaze,
        string name,
        params string[] preferredAssemblies)
    {
        foreach (var assembly in preferredAssemblies)
        {
            var value = _api!.FindClass(assembly, namespaze, name);
            if (value != 0) return (assembly, value);
        }
        foreach (var image in _api!.GetImages().Where(image =>
                     image.Name.Contains("Gley", StringComparison.OrdinalIgnoreCase) ||
                     image.Name.Contains("Traffic", StringComparison.OrdinalIgnoreCase)))
        {
            var value = _api.FindClass(image.Name, namespaze, name);
            if (value != 0) return (image.Name, value);
        }
        _context!.Log.Warning(
            "Gley.TrafficSystem.VehicleComponent was not found; SCC vehicles remain supported.");
        return (null, 0);
    }

    private nint RequireField(nint klass, string name)
    {
        var value = _api!.FindField(klass, name);
        return value != 0 ? value : throw new MissingFieldException(name);
    }

    private void RequireFieldType(nint klass, nint field, string expectedType)
    {
        var metadata = _api!.GetFields(klass).Single(candidate => candidate.Pointer == field);
        if (!string.Equals(metadata.TypeName, expectedType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Field '{metadata.Name}' is '{metadata.TypeName}', expected '{expectedType}'.");
        }
    }

    private nint RequireMethod(nint klass, string name, int arguments)
    {
        var value = _api!.FindMethod(klass, name, arguments);
        return value != 0 ? value : throw new MissingMethodException($"{name}/{arguments}");
    }

    private static void RequireSupportedBuild(IModContext context)
    {
        if (!context.Runtime.IsVerifiedGameBuild || context.Runtime.PointerSize != 8)
        {
            throw new NotSupportedException(
                $"Crash & Drift supports only the verified x64 build; got " +
                $"{context.Runtime.GameBuildFingerprint}.");
        }
    }

    private readonly record struct DriftDefaults(
        float SidewaysStiffness,
        float MinimumGrip,
        float DriftYawTorque);

    private readonly record struct VehicleTarget(
        nint GameObject,
        nint Component,
        VehicleKind Kind)
    {
        public VehicleTarget(nint gameObject, VehicleKind kind)
            : this(gameObject, 0, kind) { }
    }

    private sealed class ExplosionEffect(
        UnityObject gameObject,
        IModMeshBinding meshBinding,
        IModRendererBinding materialBinding,
        UnityVector3 position,
        float startScale,
        float maxScale,
        float duration)
    {
        public UnityObject GameObject { get; } = gameObject;
        public IModMeshBinding MeshBinding { get; } = meshBinding;
        public IModRendererBinding MaterialBinding { get; } = materialBinding;
        public UnityVector3 Position { get; } = position;
        public float StartScale { get; } = startScale;
        public float MaxScale { get; } = maxScale;
        public float Duration { get; } = duration;
        public float Age { get; set; }
    }

    private enum VehicleKind
    {
        Scc,
        Traffic,
    }
}
