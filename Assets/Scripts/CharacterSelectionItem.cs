using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

public class CharacterSelectionItem : MonoBehaviour
{
    [SerializeField] private Button button;
    [SerializeField] private TextMeshProUGUI label;

    private CharacterCardSO _card;

    public void Bind(CharacterCardSO card, Action onClick)
    {
        _card = card;
        if (label) label.text = $"{(int)card.characterNumber} - {card.characterName}";

        button.onClick.RemoveAllListeners();

        button.onClick.AddListener(() =>
        {
            Debug.Log($"[SELECT] Click: #{(int)_card.characterNumber} {_card.characterName}");
            onClick?.Invoke();
        });

        //button.onClick.AddListener(() => onClick?.Invoke());
    }

    public void SetInteractable(bool on)
    {
        button.interactable = on;
        // optional: dim when disabled
        var cg = GetComponent<CanvasGroup>();
        if (cg) cg.alpha = on ? 1f : 0f;
    }
}
