using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ManipulationChoicePanel : MonoBehaviour
{
    [SerializeField] private Button cardBtnA;
    [SerializeField] private Button cardBtnB;
    [SerializeField] private Button cardBtnC;
    [SerializeField] private TextMeshProUGUI labelA;
    [SerializeField] private TextMeshProUGUI labelB;
    [SerializeField] private TextMeshProUGUI labelC;
    [SerializeField] private Button cancelBtn;
    //[SerializeField] private TextMeshProUGUI prompt;

    private ManipulationType _a, _b, _c;
    private Action<ManipulationType, ManipulationType, ManipulationType, ManipulationType> _onDone; // chosen, discard, return, cancelSentinel

    public void Show(int actingPid, List<ManipulationType> drawn,
        Action<ManipulationType, ManipulationType, ManipulationType, ManipulationType> onDone)
    {
        //prompt.gameObject.SetActive(true);
        //if (prompt) prompt.text = $"{PlayerManager.Instance.players[actingPid].playerName}, choose a manipulation";

        gameObject.SetActive(true);
        _onDone = onDone;

        _a = drawn[0]; _b = drawn[1]; _c = drawn[2];

        if (labelA) labelA.text = ToTag(_a);
        if (labelB) labelB.text = ToTag(_b);
        if (labelC) labelC.text = ToTag(_c);

        cardBtnA.onClick.RemoveAllListeners();
        cardBtnB.onClick.RemoveAllListeners();
        cardBtnC.onClick.RemoveAllListeners();
        cancelBtn.onClick.RemoveAllListeners();

        cardBtnA.onClick.AddListener(() => Choose(_a, _b, _c));
        cardBtnB.onClick.AddListener(() => Choose(_b, _a, _c));
        cardBtnC.onClick.AddListener(() => Choose(_c, _a, _b));
        cancelBtn.onClick.AddListener(() => Cancel());
    }

    private void Choose(ManipulationType chosen, ManipulationType discard, ManipulationType ret)
    {
        gameObject.SetActive(false);
        _onDone?.Invoke(chosen, discard, ret, default);
    }

    private void Cancel()
    {
        gameObject.SetActive(false);
        _onDone?.Invoke(default, default, default, (ManipulationType)(-999));
    }

    public static string ToTag(ManipulationType m) => m switch
    {
        ManipulationType.Plus1 => "+1",
        ManipulationType.Plus2 => "+2",
        ManipulationType.Plus4 => "+4",
        ManipulationType.Minus1 => "-1",
        ManipulationType.Minus2 => "-2",
        ManipulationType.Minus3 => "-3",
        ManipulationType.Dividend => "Dividend",
        _ => "?"
    };
}
