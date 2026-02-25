using UnityEngine;
using Unity.Cinemachine;

public class SimpleCharacterZoom : MonoBehaviour
{
    public CinemachineCamera camera;
    public CinemachineCamera sprintCamera;

    public bool zoomedIn = false;
    public float zoomedInFOV = 30f;
    public float zoomedOutFOV = 30f;

    private float defaultFOVNormal;
    private float defaultFOVSprint;

    void OnEnable()
    {
        defaultFOVNormal = camera.Lens.FieldOfView;
        defaultFOVSprint = sprintCamera.Lens.FieldOfView;
    }

    public void ChangeZoom()
    {
        zoomedIn = !zoomedIn;
        if(zoomedIn)
        {
            camera.Lens.FieldOfView = zoomedInFOV;
            sprintCamera.Lens.FieldOfView = zoomedInFOV;
            Debug.Log("zoomed in");
        }        
        else
        {
            camera.Lens.FieldOfView = defaultFOVNormal;
            sprintCamera.Lens.FieldOfView = defaultFOVSprint;
            Debug.Log("zoomed out");
        }
    }
}
