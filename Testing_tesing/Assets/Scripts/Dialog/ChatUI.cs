using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Dialog
{
    // Conversation window, rapport bar, interaction prompt and crosshair. The whole UI is
    // built in code so the scene needs nothing but this component; it is a functional
    // placeholder until the real UI (feedback screen etc.) is designed.
    // Runs early so the UI exists before DialogManager.Awake reports anything.
    [DefaultExecutionOrder(-100)]
    public class ChatUI : MonoBehaviour
    {
        static readonly Color PanelColor = new Color(0.11f, 0.11f, 0.12f, 0.94f);
        static readonly Color LogColor = new Color(0.07f, 0.07f, 0.08f, 0.9f);
        static readonly Color FieldColor = new Color(0.2f, 0.2f, 0.22f, 1f);
        static readonly Color AccentColor = new Color(0.33f, 0.62f, 0.95f, 1f);
        static readonly Color PlayerTextColor = new Color(0.85f, 0.85f, 0.85f);
        static readonly Color NpcTextColor = new Color(1f, 0.86f, 0.55f);
        static readonly Color MutedColor = new Color(0.6f, 0.6f, 0.62f);
        static readonly Color ColdColor = new Color(0.35f, 0.55f, 0.95f);
        static readonly Color NeutralColor = new Color(0.72f, 0.72f, 0.74f);
        static readonly Color WarmColor = new Color(1f, 0.68f, 0.28f);
        static readonly Color PipOffColor = new Color(1f, 1f, 1f, 0.15f);
        static readonly Color PipOnColor = new Color(0.9f, 0.25f, 0.25f);

        // Rapport points per second the bar moves while catching up with the real value.
        const float BarSpeed = 30f;

        Font font;
        GameObject chatPanel;
        Text titleText;
        Text turnText;
        Text statusText;
        Text hintText;
        Text promptText;
        Text crosshair;
        Text noticeText;
        ScrollRect scroll;
        RectTransform logContent;
        InputField inputField;
        Button sendButton;

        GameObject rapportPanel;
        RectTransform barFill;
        Image barFillImage;
        RectTransform coldTick;
        RectTransform warmTick;
        RectTransform pipRow;
        Image[] strikePips = new Image[0];
        float shownRapport;

        ConversationSession session;
        Text lastPlayerLine;
        Text streamingLine;
        string streamingText;
        string pendingText;
        bool confirmLeave;
        bool queuedSubmit;
        bool scrollToBottom;
        bool focusInput;

        void Awake()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EnsureEventSystem();
            BuildUI();
            chatPanel.SetActive(false);
            rapportPanel.SetActive(false);
            SetInteractPrompt(null);
            ShowNotice(null);
        }

        // ---------- Public API ----------

        public void SetInteractPrompt(string text)
        {
            promptText.text = text ?? "";
            promptText.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }

        // Short message at the top of the screen (loading, errors). null hides it.
        public void ShowNotice(string text)
        {
            noticeText.text = text ?? "";
            noticeText.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }

        public void Open(ConversationSession newSession)
        {
            session = newSession;
            session.NpcReplyStarted += OnNpcReplyStarted;
            session.NpcToken += OnNpcToken;
            session.NpcReplyCompleted += OnNpcReplyCompleted;
            session.ReplyFailed += OnReplyFailed;
            session.TurnAnalyzed += OnTurnAnalyzed;
            session.Ended += OnEnded;

            confirmLeave = false;
            queuedSubmit = false;
            foreach (Transform child in logContent)
                Destroy(child.gameObject);

            CharacterDefinition c = session.Character;
            titleText.text = c.Name;
            foreach (ChatMessage message in session.Transcript)
                AddLine(message.role, message.content);

            SetUpRapportBar();
            chatPanel.SetActive(true);
            rapportPanel.SetActive(true);
            crosshair.gameObject.SetActive(false);
            SetInteractPrompt(null);
            inputField.text = "";
            SetStatus("");
            RefreshState();
            // Next frame, so the E that opened the chat is not typed into the field.
            focusInput = true;
        }

        public void Close()
        {
            if (session != null)
            {
                session.NpcReplyStarted -= OnNpcReplyStarted;
                session.NpcToken -= OnNpcToken;
                session.NpcReplyCompleted -= OnNpcReplyCompleted;
                session.ReplyFailed -= OnReplyFailed;
                session.TurnAnalyzed -= OnTurnAnalyzed;
                session.Ended -= OnEnded;
            }
            session = null;
            streamingLine = null;
            lastPlayerLine = null;
            chatPanel.SetActive(false);
            rapportPanel.SetActive(false);
            crosshair.gameObject.SetActive(true);
        }

        // ---------- Frame loop ----------

        void Update()
        {
            if (session == null)
                return;

            if (focusInput)
            {
                focusInput = false;
                FocusInput();
            }

            UpdateRapportBar();

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                TryLeave();
        }

        void LateUpdate()
        {
            if (!scrollToBottom)
                return;
            scrollToBottom = false;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 0f;
        }

        // ---------- Conversation flow ----------

        void Submit()
        {
            if (session == null || session.IsOver || session.IsActorBusy)
                return;

            string text = inputField.text.Trim();
            if (text.Length == 0)
            {
                FocusInput();
                return;
            }

            // The analyzer is still labelling the previous message: send as soon as it is done.
            if (session.IsAnalyzing)
            {
                queuedSubmit = true;
                return;
            }

            confirmLeave = false;
            pendingText = text;
            if (!session.SendPlayerMessage(text))
                return;
            inputField.text = "";
            lastPlayerLine = AddLine("user", text);
            RefreshState();
        }

        void OnNpcReplyStarted()
        {
            streamingText = "";
            streamingLine = AddLine("assistant", "...");
            SetStatus($"{session.Character.Name} is thinking...");
            RefreshState();
        }

        void OnNpcToken(string token)
        {
            if (streamingLine == null)
                return;
            streamingText += token;
            streamingLine.text = FormatLine("assistant", streamingText);
            scrollToBottom = true;
        }

        void OnNpcReplyCompleted(string reply)
        {
            if (streamingLine != null)
                streamingLine.text = FormatLine("assistant", reply);
            streamingLine = null;
            SetStatus("");
            RefreshState();
            FocusInput();
        }

        void OnReplyFailed(string error)
        {
            if (streamingLine != null)
                Destroy(streamingLine.gameObject);
            if (lastPlayerLine != null)
                Destroy(lastPlayerLine.gameObject);
            streamingLine = null;
            lastPlayerLine = null;
            // Give the message back so the player can resend it.
            inputField.text = pendingText;
            SetStatus("Could not get a reply: " + error);
            Debug.LogError("[Dialog] Actor LLM error: " + error);
            RefreshState();
            FocusInput();
        }

        void OnTurnAnalyzed(TurnAnalysis turn)
        {
            if (!turn.Failed)
            {
                string found = string.Join(", ", turn.Breakdown);
                // Summary on the first line (what the Console list shows), raw JSON below it.
                Debug.Log($"[Analyzer] turn {turn.Turn}: {(found.Length > 0 ? found : "nothing")} -> rapport {turn.RapportAfter:0} ({turn.RapportDelta:+0;-0;0})\n" +
                          $"Player: {turn.PlayerMessage}\n{turn.RawJson}");
            }
            UpdateStrikePips();
            RefreshState();

            if (queuedSubmit)
            {
                queuedSubmit = false;
                Submit();
            }
        }

        void OnEnded(string reason)
        {
            queuedSubmit = false;
            string name = session.Character.Name;
            SetStatus(reason == ConversationSession.EndBoundaryStrikes
                ? $"{name} has ended the conversation. Press Esc to close."
                : "The conversation has ended. Press Esc to close.");
            RefreshState();
        }

        void TryLeave()
        {
            if (session.IsOver || session.ReachedMinTurns || confirmLeave)
            {
                string reason = session.IsOver ? session.EndReason
                    : session.ReachedMinTurns ? "player_ended" : "player_left_early";
                DialogManager.Instance.EndConversation(reason);
                return;
            }

            confirmLeave = true;
            SetStatus($"Leaving now ends the conversation early (minimum {session.Character.Rules.MinTurns} turns). Press Esc again to leave.");
        }

        void RefreshState()
        {
            CharacterDefinition c = session.Character;
            turnText.text = $"Turn {session.PlayerTurns} / {c.Rules.MaxTurns}" +
                (session.ReachedMinTurns ? "" : $"   ·   at least {c.Rules.MinTurns} before leaving") +
                $"   ·   {c.Difficulty}";

            // Typing is allowed while the analyzer works; sending waits for it (queuedSubmit).
            bool canType = !session.IsOver && !session.IsActorBusy;
            inputField.interactable = canType;
            sendButton.interactable = canType;
            hintText.text = session.IsOver ? "Esc  close" : "Enter  send      Esc  leave";
        }

        void SetStatus(string text)
        {
            statusText.text = text;
        }

        void FocusInput()
        {
            if (!inputField.interactable)
                return;
            EventSystem.current?.SetSelectedGameObject(inputField.gameObject);
            inputField.ActivateInputField();
        }

        Text AddLine(string role, string content)
        {
            Text line = CreateText("Line", logContent, 24, role == "user" ? PlayerTextColor : NpcTextColor);
            line.text = FormatLine(role, content);
            line.supportRichText = true;
            scrollToBottom = true;
            return line;
        }

        string FormatLine(string role, string content)
        {
            string speaker = role == "user" ? "You" : session.Character.Name;
            // Break up '<' so the player can't inject rich-text tags.
            return $"<b>{speaker}</b>\n{content.Replace("<", "<​")}";
        }

        void OnInputEndEdit(string _)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
                Submit();
        }

        // ---------- Rapport bar ----------

        void SetUpRapportBar()
        {
            RapportRules rules = session.Character.Rapport;
            shownRapport = session.Rapport.Value;
            SetTick(coldTick, rules.ColdBelow);
            SetTick(warmTick, rules.WarmAbove);

            foreach (Transform child in pipRow)
                Destroy(child.gameObject);
            strikePips = new Image[session.Rapport.MaxStrikes];
            for (int i = 0; i < strikePips.Length; i++)
            {
                strikePips[i] = CreatePanel("Strike", pipRow, PipOffColor);
                strikePips[i].gameObject.AddComponent<LayoutElement>().preferredWidth = 14;
            }
            UpdateStrikePips();
            UpdateRapportBar();
        }

        // Eases the bar towards the real value, like a chess evaluation bar settling.
        void UpdateRapportBar()
        {
            shownRapport = Mathf.MoveTowards(shownRapport, session.Rapport.Value, BarSpeed * Time.deltaTime);
            float t = shownRapport / 100f;
            barFill.anchorMax = new Vector2(1f, t);
            barFillImage.color = t < 0.5f
                ? Color.Lerp(ColdColor, NeutralColor, t * 2f)
                : Color.Lerp(NeutralColor, WarmColor, (t - 0.5f) * 2f);
        }

        void UpdateStrikePips()
        {
            for (int i = 0; i < strikePips.Length; i++)
                strikePips[i].color = i < session.Rapport.Strikes ? PipOnColor : PipOffColor;
        }

        static void SetTick(RectTransform tick, float value)
        {
            tick.anchorMin = new Vector2(0f, value / 100f);
            tick.anchorMax = new Vector2(1f, value / 100f);
        }

        // ---------- UI construction ----------

        void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
                return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(transform, false);
        }

        void BuildUI()
        {
            var canvasGo = new GameObject("Dialog Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            RectTransform root = canvasGo.GetComponent<RectTransform>();

            crosshair = CreateText("Crosshair", root, 28, new Color(1f, 1f, 1f, 0.7f));
            crosshair.text = "+";
            crosshair.alignment = TextAnchor.MiddleCenter;
            Place(crosshair.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(40, 40));

            promptText = CreateText("Interact Prompt", root, 30, Color.white);
            promptText.alignment = TextAnchor.MiddleCenter;
            promptText.gameObject.AddComponent<Shadow>().effectDistance = new Vector2(2, -2);
            Place(promptText.rectTransform, new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.4f), new Vector2(700, 50));

            noticeText = CreateText("Notice", root, 24, Color.white);
            noticeText.alignment = TextAnchor.MiddleCenter;
            noticeText.gameObject.AddComponent<Shadow>().effectDistance = new Vector2(2, -2);
            Place(noticeText.rectTransform, new Vector2(0.5f, 0.95f), new Vector2(0.5f, 0.95f), new Vector2(1200, 40));

            BuildChatPanel(root);
            BuildRapportBar(root);
        }

        // Vertical bar left of the chat panel: fill = rapport, ticks = cold/warm thresholds,
        // red pips underneath = boundary strikes.
        void BuildRapportBar(RectTransform root)
        {
            rapportPanel = CreatePanel("Rapport", root, PanelColor).gameObject;
            RectTransform panel = rapportPanel.GetComponent<RectTransform>();
            panel.anchorMin = new Vector2(0.505f, 0.05f);
            panel.anchorMax = new Vector2(0.54f, 0.95f);
            panel.offsetMin = panel.offsetMax = Vector2.zero;

            Image track = CreatePanel("Track", panel, LogColor);
            RectTransform trackRect = track.rectTransform;
            trackRect.anchorMin = new Vector2(0.25f, 0f);
            trackRect.anchorMax = new Vector2(0.75f, 1f);
            trackRect.offsetMin = new Vector2(0, 90);
            trackRect.offsetMax = new Vector2(0, -16);

            barFillImage = CreatePanel("Fill", trackRect, NeutralColor);
            barFill = barFillImage.rectTransform;
            barFill.anchorMin = Vector2.zero;
            barFill.anchorMax = new Vector2(1f, 0.5f);
            barFill.offsetMin = barFill.offsetMax = Vector2.zero;

            coldTick = CreatePanel("Cold Tick", trackRect, new Color(1f, 1f, 1f, 0.35f)).rectTransform;
            coldTick.sizeDelta = new Vector2(12, 2);
            warmTick = CreatePanel("Warm Tick", trackRect, new Color(1f, 1f, 1f, 0.35f)).rectTransform;
            warmTick.sizeDelta = new Vector2(12, 2);

            var pips = new GameObject("Strikes", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            pipRow = pips.GetComponent<RectTransform>();
            pipRow.SetParent(panel, false);
            pipRow.anchorMin = new Vector2(0f, 0f);
            pipRow.anchorMax = new Vector2(1f, 0f);
            pipRow.pivot = new Vector2(0.5f, 0f);
            pipRow.sizeDelta = new Vector2(0, 14);
            pipRow.anchoredPosition = new Vector2(0, 50);
            var pipLayout = pips.GetComponent<HorizontalLayoutGroup>();
            pipLayout.spacing = 6;
            pipLayout.childAlignment = TextAnchor.MiddleCenter;
            pipLayout.childControlWidth = pipLayout.childControlHeight = true;
            pipLayout.childForceExpandWidth = false;

            Text label = CreateText("Label", panel, 16, MutedColor);
            label.text = "Rapport";
            label.alignment = TextAnchor.MiddleCenter;
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 0f);
            labelRect.pivot = new Vector2(0.5f, 0f);
            labelRect.sizeDelta = new Vector2(0, 24);
            labelRect.anchoredPosition = new Vector2(0, 16);
        }

        void BuildChatPanel(RectTransform root)
        {
            chatPanel = CreatePanel("Chat Panel", root, PanelColor).gameObject;
            RectTransform panel = chatPanel.GetComponent<RectTransform>();
            panel.anchorMin = new Vector2(0.55f, 0.05f);
            panel.anchorMax = new Vector2(0.97f, 0.95f);
            panel.offsetMin = panel.offsetMax = Vector2.zero;

            var layout = chatPanel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 20, 16);
            layout.spacing = 10;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            titleText = CreateText("Title", panel, 38, Color.white);
            titleText.fontStyle = FontStyle.Bold;
            turnText = CreateText("Turns", panel, 20, MutedColor);

            // Scrolling message log.
            Image logBg = CreatePanel("Log", panel, LogColor);
            logBg.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1;
            logBg.gameObject.AddComponent<RectMask2D>();
            scroll = logBg.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30;
            scroll.viewport = logBg.rectTransform;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            logContent = contentGo.GetComponent<RectTransform>();
            logContent.SetParent(logBg.transform, false);
            logContent.anchorMin = new Vector2(0, 1);
            logContent.anchorMax = new Vector2(1, 1);
            logContent.pivot = new Vector2(0.5f, 1);
            logContent.offsetMin = logContent.offsetMax = Vector2.zero;
            var contentLayout = contentGo.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(16, 16, 14, 14);
            contentLayout.spacing = 16;
            contentLayout.childControlWidth = contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = logContent;

            statusText = CreateText("Status", panel, 20, MutedColor);
            statusText.fontStyle = FontStyle.Italic;

            // Input row: text field + send button.
            var row = new GameObject("Input Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(panel, false);
            row.AddComponent<LayoutElement>().preferredHeight = 96;
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 10;
            rowLayout.childControlWidth = rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = true;

            inputField = CreateInputField(row.transform);
            inputField.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            sendButton = CreateButton(row.transform, "Send");
            sendButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 140;
            sendButton.onClick.AddListener(Submit);

            hintText = CreateText("Hint", panel, 18, MutedColor);
            hintText.alignment = TextAnchor.MiddleRight;
        }

        InputField CreateInputField(Transform parent)
        {
            Image bg = CreatePanel("Input", parent, FieldColor);
            var field = bg.gameObject.AddComponent<InputField>();
            field.targetGraphic = bg;

            Text placeholder = CreateText("Placeholder", bg.transform, 22, MutedColor);
            placeholder.text = "Say something...";
            placeholder.fontStyle = FontStyle.Italic;
            Stretch(placeholder.rectTransform, 14, 10);

            Text text = CreateText("Text", bg.transform, 22, Color.white);
            text.supportRichText = false;
            Stretch(text.rectTransform, 14, 10);

            field.textComponent = text;
            field.placeholder = placeholder;
            field.lineType = InputField.LineType.MultiLineSubmit;
            field.characterLimit = 500;
            field.caretColor = Color.white;
            field.selectionColor = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.4f);
            field.onEndEdit.AddListener(OnInputEndEdit);
            return field;
        }

        Button CreateButton(Transform parent, string label)
        {
            Image bg = CreatePanel("Button " + label, parent, AccentColor);
            var button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            Text text = CreateText("Label", bg.transform, 24, Color.white);
            text.text = label;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            Stretch(text.rectTransform, 0, 0);
            return button;
        }

        Image CreatePanel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        Text CreateText(string name, Transform parent, int size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        static void Place(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }

        static void Stretch(RectTransform rect, float padX, float padY)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padX, padY);
            rect.offsetMax = new Vector2(-padX, -padY);
        }
    }
}
