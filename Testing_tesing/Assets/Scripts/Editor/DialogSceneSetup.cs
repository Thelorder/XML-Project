using Dialog;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

// One-click wiring of the test scene: player controller, interaction, dialog system
// and an NPC capsule. Safe to run more than once - it only adds what is missing.
public static class DialogSceneSetup
{
    const string NpcName = "NPC_Maya";
    const string NpcMaterialPath = "Assets/Materials/NPC_Maya.mat";

    [MenuItem("Dialog/Set Up Test Scene")]
    public static void SetUpScene()
    {
        GameObject player = GameObject.Find("player");
        Camera camera = Camera.main;
        if (player == null || camera == null)
        {
            EditorUtility.DisplayDialog("Dialog setup", "Expected a 'player' object and a camera tagged MainCamera.", "OK");
            return;
        }

        SetUpPlayer(player, camera);
        SetUpNpc();
        SetUpDialogSystem();

        EditorSceneManager.MarkSceneDirty(player.scene);
        EditorSceneManager.SaveScene(player.scene);
        Debug.Log("[Dialog] Test scene set up. Press Play, walk to the capsule and press E.");
    }

    static void SetUpPlayer(GameObject player, Camera camera)
    {
        // The old CameraMovement script was deleted; drop its leftover reference.
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(camera.gameObject);
        Undo.RecordObject(camera.transform, "Dialog setup");
        camera.transform.localPosition = Vector3.zero;

        // Stand the player on the floor (the floor plane is above y = 0).
        Vector3 position = player.transform.position;
        if (Physics.Raycast(new Vector3(position.x, position.y + 20f, position.z), Vector3.down, out RaycastHit hit, 50f))
        {
            Undo.RecordObject(player.transform, "Dialog setup");
            player.transform.position = new Vector3(position.x, hit.point.y + 0.05f, position.z);
        }

        if (!player.TryGetComponent(out CharacterController controller))
        {
            controller = Undo.AddComponent<CharacterController>(player);
            controller.height = 1.8f;
            controller.radius = 0.3f;
            controller.center = new Vector3(0f, 0.9f, 0f);
        }
        if (!player.TryGetComponent(out PlayerMovement _))
            Undo.AddComponent<PlayerMovement>(player);
        if (!camera.TryGetComponent(out PlayerInteractor _))
            Undo.AddComponent<PlayerInteractor>(camera.gameObject);
    }

    static void SetUpNpc()
    {
        if (GameObject.Find(NpcName) != null)
            return;

        GameObject npc = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        Undo.RegisterCreatedObjectUndo(npc, "Dialog setup");
        npc.name = NpcName;

        // Beside the end of the table, standing on the floor.
        var spot = new Vector3(-2.4f, 0f, 2f);
        if (Physics.Raycast(spot + Vector3.up * 20f, Vector3.down, out RaycastHit hit, 50f))
            spot.y = hit.point.y;
        npc.transform.position = spot + Vector3.up; // capsule pivot is at its centre
        npc.transform.rotation = Quaternion.LookRotation(Vector3.right);

        npc.AddComponent<NpcInteractable>().characterId = "maya";
        npc.GetComponent<MeshRenderer>().sharedMaterial = GetOrCreateNpcMaterial();
    }

    static Material GetOrCreateNpcMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(NpcMaterialPath);
        if (material != null)
            return material;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            return null;
        material = new Material(shader);
        material.SetColor("_BaseColor", new Color(0.55f, 0.35f, 0.75f));
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        AssetDatabase.CreateAsset(material, NpcMaterialPath);
        return material;
    }

    static void SetUpDialogSystem()
    {
        if (Object.FindAnyObjectByType<DialogManager>() == null)
        {
            var system = new GameObject("Dialog System");
            Undo.RegisterCreatedObjectUndo(system, "Dialog setup");
            system.AddComponent<DialogManager>(); // pulls in ChatUI via RequireComponent
        }

        if (Object.FindAnyObjectByType<EventSystem>() == null)
        {
            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            Undo.RegisterCreatedObjectUndo(eventSystem, "Dialog setup");
        }
    }
}
