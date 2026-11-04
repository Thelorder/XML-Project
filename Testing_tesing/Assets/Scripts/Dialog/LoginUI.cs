using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Dialog
{
    // Full-screen corporate-style login shown at game start. Gameplay components are
    // disabled until the player signs in.
    [DefaultExecutionOrder(-50)] // after ChatUI (-100), which creates the EventSystem
    public class LoginUI : MonoBehaviour
    {
        public static event Action LoggedIn;

        // Gameplay scripts to freeze while the login is open (matched by class name,
        // so it works whatever namespace they are in).
        static readonly string[] GameplayTypes = { "PlayerMovement", "MouseLook", "PlayerInteractor" };

        static readonly Color Brand = new Color32(0x0B, 0x5F, 0xFF, 255);
        static readonly Color BrandHover = new Color32(0x09, 0x4C, 0xD1, 255);
        static readonly Color BrandPressed = new Color32(0x07, 0x3B, 0xA3, 255);
        static readonly Color Ink = new Color32(0x1A, 0x1F, 0x36, 255);
        static readonly Color Muted = new Color32(0x6B, 0x72, 0x80, 255);
        static readonly Color Border = new Color32(0xD1, 0xD5, 0xDB, 255);
        static readonly Color Danger = new Color32(0xD9, 0x2D, 0x20, 255);
        static readonly Color GradTop = new Color32(0x0A, 0x1E, 0x4A, 255);
        static readonly Color GradBottom = new Color32(0x0B, 0x5F, 0xFF, 255);

        Font font;
        GameObject canvasGo;
        CanvasGroup fade;
        Text titleText, subtitleText, errorText, switchPrompt, switchLink;
        InputField userField, passField, confirmField;
        Text buttonLabel;
        Button submitButton;
        Toggle rememberToggle;
        bool registerMode;
        readonly List<Behaviour> frozen = new List<Behaviour>();
        static readonly Dictionary<int, Sprite> roundedCache = new Dictionary<int, Sprite>();

        void Awake()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (FindAnyObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            foreach (MonoBehaviour mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (mb != null && mb != this && Array.IndexOf(GameplayTypes, mb.GetType().Name) >= 0)
                {
                    mb.enabled = false;
                    frozen.Add(mb);
                }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            BuildUI();
            SetMode(false);
            userField.text = PlayerPrefs.GetString("Dialog.LastUser", "");
        }

        void Start() { Focus(userField.text.Length > 0 ? passField : userField); }

        void Update()
        {
            if (fade.alpha < 1f) fade.alpha = Mathf.MoveTowards(fade.alpha, 1f, Time.unscaledDeltaTime * 2.5f);

            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            if (kb.tabKey.wasPressedThisFrame)
            {
                InputField[] order = registerMode
                    ? new[] { userField, passField, confirmField }
                    : new[] { userField, passField };
                int i = Array.IndexOf(order, EventSystem.current.currentSelectedGameObject?.GetComponent<InputField>());
                Focus(order[(i + 1) % order.Length]);
            }
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                Submit();
        }

        // ---------- Logic ----------

        void SetMode(bool register)
        {
            registerMode = register;
            titleText.text = register ? "Create your account" : "Sign in";
            subtitleText.text = register ? "Choose a pseudonym and a password. Stored only on this computer."
                                         : "Welcome back. Enter your details to continue.";
            buttonLabel.text = register ? "Create account" : "Sign in";
            switchPrompt.text = register ? "Already have an account?" : "New here?";
            switchLink.text = register ? "Sign in" : "Create account";
            confirmField.transform.parent.gameObject.SetActive(register);
            rememberToggle.transform.parent.gameObject.SetActive(!register);
            errorText.text = "";
            passField.text = confirmField.text = "";
        }

        void Submit()
        {
            string error;
            if (registerMode)
            {
                if (passField.text != confirmField.text) { ShowError("Passwords do not match."); return; }
                if (!AuthService.TryRegister(userField.text, passField.text, out error)) { ShowError(error); return; }
            }
            else if (!AuthService.TryLogin(userField.text, passField.text, out error))
            {
                ShowError(error);
                passField.text = "";
                Focus(passField);
                return;
            }

            if (registerMode || rememberToggle.isOn)
                PlayerPrefs.SetString("Dialog.LastUser", AuthService.CurrentUserName);
            else
                PlayerPrefs.DeleteKey("Dialog.LastUser");

            Finish();
        }

        void Finish()
        {
            foreach (Behaviour b in frozen) if (b != null) b.enabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            LoggedIn?.Invoke();
            Destroy(canvasGo);
            Destroy(this);
        }

        void ShowError(string msg) { errorText.text = msg; }

        void Focus(InputField f)
        {
            EventSystem.current.SetSelectedGameObject(f.gameObject);
            f.ActivateInputField();
        }

        // ---------- UI construction ----------

        void BuildUI()
        {
            canvasGo = new GameObject("Login Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            fade = canvasGo.GetComponent<CanvasGroup>();
            fade.alpha = 0f;
            var root = (RectTransform)canvasGo.transform;

            // Right side: white background.
            Image right = Panel("Right", root, Color.white);
            Anchor(right.rectTransform, 0.5f, 0f, 1f, 1f);

            // Left side: gradient brand panel.
            Image left = Panel("Left", root, Color.white);
            left.sprite = GradientSprite(GradTop, GradBottom);
            Anchor(left.rectTransform, 0f, 0f, 0.5f, 1f);
            BuildBrandPanel(left.rectTransform);

            // Form column, centered in the right half.
            var form = new GameObject("Form", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var formRect = (RectTransform)form.transform;
            formRect.SetParent(right.transform, false);
            formRect.anchorMin = formRect.anchorMax = formRect.pivot = new Vector2(0.5f, 0.5f);
            formRect.sizeDelta = new Vector2(480, 0);
            var v = form.GetComponent<VerticalLayoutGroup>();
            v.spacing = 8;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            form.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            titleText = Label(form.transform, "", 44, Ink, FontStyle.Bold);
            subtitleText = Label(form.transform, "", 20, Muted, FontStyle.Normal);
            Spacer(form.transform, 18);

            userField = LabeledField(form.transform, "Username", "your.pseudonym", false);
            passField = LabeledField(form.transform, "Password", "••••••••", true);
            confirmField = LabeledField(form.transform, "Confirm password", "••••••••", true);

            rememberToggle = BuildRemember(form.transform);

            errorText = Label(form.transform, "", 19, Danger, FontStyle.Normal);
            Spacer(form.transform, 6);

            submitButton = BuildButton(form.transform, out buttonLabel);
            submitButton.onClick.AddListener(Submit);
            Spacer(form.transform, 10);
            BuildSwitchRow(form.transform);
        }

        void BuildBrandPanel(RectTransform parent)
        {
            var box = new GameObject("Brand", typeof(RectTransform), typeof(VerticalLayoutGroup));
            var rect = (RectTransform)box.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.1f, 0.15f);
            rect.anchorMax = new Vector2(0.9f, 0.85f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var v = box.GetComponent<VerticalLayoutGroup>();
            v.spacing = 14; v.childAlignment = TextAnchor.UpperLeft;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;

            //Image logo = Panel("Logo", box.transform, Color.white);
            //logo.sprite = Rounded(14); logo.type = Image.Type.Sliced;
            //logo.gameObject.AddComponent<LayoutElement>().preferredHeight = 64;
            //logo.GetComponent<LayoutElement>().preferredWidth = 64;
            //logo.GetComponent<LayoutElement>().flexibleWidth = 0;
            //Text mark = Label(logo.transform, "E", 38, Brand, FontStyle.Bold);
            //mark.alignment = TextAnchor.MiddleCenter;
            //Stretch(mark.rectTransform, 0, 0);

            Spacer(box.transform, 20);
            Label(box.transform, "Empathic\nCommunication\nSimulator", 64, Color.white, FontStyle.Bold);
            Spacer(box.transform, 10);
            Label(box.transform, "Practice difficult conversations in a safe space and get a personal report on how you communicate.", 24,
                  new Color(1, 1, 1, 0.8f), FontStyle.Normal);
            Spacer(box.transform, 30);
            Label(box.transform, "•  Realistic AI companions\n•  Live rapport feedback\n•  Progress tracking across sessions\n•  Runs fully offline on your machine", 22,
                  new Color(1, 1, 1, 0.9f), FontStyle.Normal);
        }

        InputField LabeledField(Transform parent, string label, string placeholder, bool password)
        {
            var group = new GameObject(label + " Group", typeof(RectTransform), typeof(VerticalLayoutGroup));
            group.transform.SetParent(parent, false);
            var v = group.GetComponent<VerticalLayoutGroup>();
            v.spacing = 6; v.padding = new RectOffset(0, 0, 6, 6);
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            Label(group.transform, label, 18, Ink, FontStyle.Bold);

            // Outer image = border, inner image = white fill, so the border can recolor on focus.
            Image border = Panel("Border", group.transform, Border);
            border.sprite = Rounded(10); border.type = Image.Type.Sliced;
            border.gameObject.AddComponent<LayoutElement>().preferredHeight = 58;
            Image fill = Panel("Fill", border.transform, Color.white);
            fill.sprite = Rounded(8); fill.type = Image.Type.Sliced;
            Stretch(fill.rectTransform, 2, 2);

            var field = border.gameObject.AddComponent<InputField>();
            field.targetGraphic = fill;
            Text ph = Label(fill.transform, placeholder, 22, new Color32(0x9C, 0xA3, 0xAF, 255), FontStyle.Italic);
            Stretch(ph.rectTransform, 16, 8); ph.alignment = TextAnchor.MiddleLeft;
            Text tx = Label(fill.transform, "", 22, Ink, FontStyle.Normal);
            Stretch(tx.rectTransform, 16, 8); tx.alignment = TextAnchor.MiddleLeft;
            tx.supportRichText = false;
            field.textComponent = tx;
            field.placeholder = ph;
            field.caretColor = Ink;
            field.selectionColor = new Color(Brand.r, Brand.g, Brand.b, 0.3f);
            field.characterLimit = 64;
            field.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
            field.inputType = password ? InputField.InputType.Password : InputField.InputType.Standard;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        Toggle BuildRemember(Transform parent)
        {
            var row = new GameObject("Remember Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            var h = row.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 10; h.padding = new RectOffset(0, 0, 4, 4);
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = h.childControlHeight = false;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            row.AddComponent<LayoutElement>().preferredHeight = 34;

            Image box = Panel("Box", row.transform, Border);
            box.sprite = Rounded(6); box.type = Image.Type.Sliced;
            ((RectTransform)box.transform).sizeDelta = new Vector2(26, 26);
            Image inner = Panel("Inner", box.transform, Color.white);
            inner.sprite = Rounded(5); inner.type = Image.Type.Sliced;
            Stretch(inner.rectTransform, 2, 2);
            Image check = Panel("Check", inner.transform, Brand);
            check.sprite = Rounded(3); check.type = Image.Type.Sliced;
            Stretch(check.rectTransform, 5, 5);

            var toggle = box.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = inner;
            toggle.graphic = check;
            toggle.isOn = PlayerPrefs.HasKey("Dialog.LastUser");

            Text t = Label(row.transform, "Remember my username", 19, Muted, FontStyle.Normal);
            ((RectTransform)t.transform).sizeDelta = new Vector2(300, 26);
            return toggle;
        }

        Button BuildButton(Transform parent, out Text label)
        {
            Image bg = Panel("Submit", parent, Color.white);
            bg.sprite = Rounded(10); bg.type = Image.Type.Sliced;
            bg.gameObject.AddComponent<LayoutElement>().preferredHeight = 60;
            var b = bg.gameObject.AddComponent<Button>();
            b.targetGraphic = bg;
            b.transition = Selectable.Transition.ColorTint;
            b.colors = new ColorBlock
            {
                normalColor = Brand,
                highlightedColor = BrandHover,
                pressedColor = BrandPressed,
                selectedColor = Brand,
                disabledColor = Border,
                colorMultiplier = 1f,
                fadeDuration = 0.1f
            };
            label = Label(bg.transform, "", 24, Color.white, FontStyle.Bold);
            label.alignment = TextAnchor.MiddleCenter;
            Stretch(label.rectTransform, 0, 0);
            return b;
        }

        void BuildSwitchRow(Transform parent)
        {
            var row = new GameObject("Switch Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            var h = row.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            row.AddComponent<LayoutElement>().preferredHeight = 34;

            switchPrompt = Label(row.transform, "", 19, Muted, FontStyle.Normal);
            switchPrompt.horizontalOverflow = HorizontalWrapMode.Overflow;
            switchLink = Label(row.transform, "", 19, Brand, FontStyle.Bold);
            switchLink.horizontalOverflow = HorizontalWrapMode.Overflow;
            switchLink.raycastTarget = true;
            var btn = switchLink.gameObject.AddComponent<Button>();
            btn.targetGraphic = switchLink;
            btn.onClick.AddListener(() => SetMode(!registerMode));
        }

        // ---------- Helpers ----------

        Text Label(Transform parent, string text, int size, Color color, FontStyle style)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font; t.text = text; t.fontSize = size; t.color = color; t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        static Image Panel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        static void Spacer(Transform parent, float height)
        {
            var go = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = height;
        }

        static void Anchor(RectTransform r, float x0, float y0, float x1, float y1)
        {
            r.anchorMin = new Vector2(x0, y0); r.anchorMax = new Vector2(x1, y1);
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        static void Stretch(RectTransform r, float px, float py)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(px, py); r.offsetMax = new Vector2(-px, -py);
        }

        // 9-sliceable rounded rectangle generated at runtime (no asset needed).
        static Sprite Rounded(int radius)
        {
            if (roundedCache.TryGetValue(radius, out Sprite cached)) return cached;
            int size = radius * 2 + 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(radius - (x + 0.5f), 0f, (x + 0.5f) - (size - radius));
                    float dy = Mathf.Max(radius - (y + 0.5f), 0f, (y + 0.5f) - (size - radius));
                    float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    tex.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            tex.Apply();
            var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                                  SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            roundedCache[radius] = s;
            return s;
        }

        static Sprite GradientSprite(Color top, Color bottom)
        {
            var tex = new Texture2D(1, 128, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 128; y++) tex.SetPixel(0, y, Color.Lerp(bottom, top, y / 127f));
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 128), new Vector2(0.5f, 0.5f));
        }
    }
}