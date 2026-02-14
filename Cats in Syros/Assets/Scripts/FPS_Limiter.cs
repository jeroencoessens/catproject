using UnityEngine;
using TMPro;
using Unity.VisualScripting;

public class FPS_Limiter : MonoBehaviour
{
    public int FPS = 120;
    public TMP_Text m_FPSText;
    public bool showFpsText = true;
    
    void Start()
    {
        Application.targetFrameRate = FPS;
    }

    void Update()
    {
        if(showFpsText)
            UpdateFPSText();
    }
    
    private void UpdateFPSText()
    {
        var fps = ((int)1.0f / Time.smoothDeltaTime);
        var color = Color.white;
        if (fps > 90) color = Color.green;
        if (fps < 60) color = Color.yellow;
        if (fps < 45) color = Color.orange;
        if (fps < 35) color = Color.darkOrange;
        if (fps < 25) color = Color.red;

        if(m_FPSText) m_FPSText.text = "<color=#" + color.ToHexString() + ">" + fps.ToString("F0");
    }
}
