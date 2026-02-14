using UnityEngine;

public class Graphics_Manager : MonoBehaviour
{
    public Light lightSource;
    public int customShadowResolution = 256;
    
    void Start()
    {
        if (lightSource)
        {
            lightSource.shadowCustomResolution = customShadowResolution;
        }
    }
}
