
using UnityEngine;
using UnityEngine.InputSystem;

public class MouseLook : MonoBehaviour
{
    [SerializeField] private float sensitivity = 0.1f;
    [SerializeField] private Transform cameraTransform;

    private float pitch = 0f;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        if (Mouse.current == null)
            return;

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();

        float mouseX = mouseDelta.x * sensitivity;
        float mouseY = mouseDelta.y * sensitivity;

        // Horizontal rotation: rotate the Player.
        if (transform.parent != null)
        {
            transform.parent.Rotate(Vector3.up * mouseX);
        }

        // Vertical rotation: tilt the camera only.
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, -80f, 80f);

        if (cameraTransform != null)
        {
            cameraTransform.localRotation =
                Quaternion.Euler(pitch, 0f, 0f);
        }

        // Optional: release the mouse when pressing Escape.
        if (Keyboard.current != null &&
            Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
