using UnityEngine;

namespace Dialog
{
    // Marks a GameObject (with a collider) as someone the player can talk to.
    // Only the id lives here; everything else comes from Characters/<id>.xml.
    public class NpcInteractable : MonoBehaviour
    {
        [Tooltip("Must match the id attribute of a character XML file.")]
        public string characterId = "maya";

        public void FaceTowards(Vector3 position)
        {
            Vector3 direction = position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
