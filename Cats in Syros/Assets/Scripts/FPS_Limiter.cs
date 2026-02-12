using UnityEngine;

public class FPS_Limiter : MonoBehaviour
{
    public int FPS = 120;
    
    void Start()
    {
        Application.targetFrameRate = FPS;
    }
}
