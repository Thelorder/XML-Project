using UnityEngine;
using UnityEngine.InputSystem;

namespace Dialog
{
    // Sits on the player camera: looks for an NPC in front of the player and starts
    // a conversation when E is pressed.
    public class PlayerInteractor : MonoBehaviour
    {
        [SerializeField] private float range = 3f;

        void Update()
        {
            DialogManager manager = DialogManager.Instance;
            if (manager == null || manager.IsInConversation)
                return;

            NpcInteractable target = null;
            if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, range))
                target = hit.collider.GetComponentInParent<NpcInteractable>();

            if (target == null)
            {
                manager.UI.SetInteractPrompt(null);
                return;
            }

            CharacterDefinition character = manager.FindCharacter(target.characterId);
            string name = character != null ? character.Name : target.characterId;
            manager.UI.SetInteractPrompt($"[E]  Talk to {name}");

            if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
                manager.StartConversation(target);
        }

        void OnDisable()
        {
            if (DialogManager.Instance != null)
                DialogManager.Instance.UI.SetInteractPrompt(null);
        }
    }
}
