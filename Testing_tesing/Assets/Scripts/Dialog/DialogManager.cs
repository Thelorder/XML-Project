using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Dialog
{
    // Entry point of the dialog system: loads the XML data, warms up the model
    // and starts/ends conversations. One per scene, next to ChatUI.
    [RequireComponent(typeof(ChatUI))]
    public class DialogManager : MonoBehaviour
    {
        public static DialogManager Instance { get; private set; }

        public ChatUI UI { get; private set; }
        public GameSettings Settings { get; private set; }
        public IReadOnlyList<CharacterDefinition> Characters => characters;
        public bool IsInConversation => current != null;

        // Disabled while a conversation is open.
        [SerializeField] private Behaviour[] playerControls;

        List<CharacterDefinition> characters = new List<CharacterDefinition>();
        OllamaClient client;
        ConversationSession current;

        void Awake()
        {
            Instance = this;
            UI = GetComponent<ChatUI>();

            var errors = new List<string>();
            Settings = XmlDataLoader.LoadSettings(errors);
            characters = XmlDataLoader.LoadCharacters(errors);
            foreach (string error in errors)
                Debug.LogError("[Dialog] " + error);
            Debug.Log($"[Dialog] Loaded {characters.Count} character(s): {string.Join(", ", characters.Select(c => c.Id))}");

            if (Settings == null)
            {
                UI.ShowNotice("settings.xml is missing or invalid - see the Console.");
                enabled = false;
                return;
            }

            client = new OllamaClient(Settings.OllamaUrl, Settings.KeepAlive);

            if (playerControls == null || playerControls.Length == 0)
            {
                playerControls = new Behaviour[]
                {
                    FindAnyObjectByType<PlayerMovement>(),
                    FindAnyObjectByType<MouseLook>(),
                    FindAnyObjectByType<PlayerInteractor>(),
                }.Where(b => b != null).ToArray();
            }
        }

        void Start()
        {
            if (client == null)
                return;
            UI.ShowNotice($"Loading {Settings.Actor.Model}...");
            client.Warmup(Settings.Actor, ok =>
                UI.ShowNotice(ok ? null : $"Could not reach Ollama at {Settings.OllamaUrl}. Is it running?"));
        }

        void OnDestroy()
        {
            current?.End("quit");
            if (Instance == this)
                Instance = null;
        }

        public CharacterDefinition FindCharacter(string id)
        {
            return characters.FirstOrDefault(c => c.Id == id);
        }

        public void StartConversation(NpcInteractable npc)
        {
            if (IsInConversation || client == null)
                return;

            CharacterDefinition character = FindCharacter(npc.characterId);
            if (character == null)
            {
                UI.ShowNotice($"No valid character XML with id '{npc.characterId}'.");
                return;
            }

            npc.FaceTowards(Camera.main != null ? Camera.main.transform.position : npc.transform.position);

            current = new ConversationSession(character, client, Settings.Actor);
            SetPlayerControls(false);
            UI.Open(current);
        }

        // Called by ChatUI when the player closes the conversation window.
        public void EndConversation(string reason)
        {
            if (current == null)
                return;

            current.End(reason);
            Debug.Log($"[Dialog] Conversation with {current.Character.Name} ended ({current.EndReason}) after {current.PlayerTurns} turn(s).");
            // TODO (weeks 5-6): run the Evaluator on current.Transcript and show the report.

            current = null;
            UI.Close();
            // Wait a frame so the key that closed the chat (Esc) isn't also seen by MouseLook.
            StartCoroutine(EnableControlsNextFrame());
        }

        IEnumerator EnableControlsNextFrame()
        {
            yield return null;
            SetPlayerControls(true);
        }

        void SetPlayerControls(bool on)
        {
            foreach (Behaviour behaviour in playerControls)
            {
                if (behaviour != null)
                    behaviour.enabled = on;
            }
            if (!on)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }
}
