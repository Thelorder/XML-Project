using UnityEngine;

public class CameraMovement : MonoBehaviour
{
    public float speed = 5f;

    void Update()
    {
        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");

        Vector3 movement = transform.right * x
                         + transform.forward * z;

        movement.y = 0f;

        transform.position +=
            movement.normalized * speed * Time.deltaTime;
    }
}
