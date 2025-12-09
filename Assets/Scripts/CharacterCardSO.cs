using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "Character Card", menuName = "Cards/Character Card", order = 1)]
public class CharacterCardSO : ScriptableObject
{
    public int characterNumber;                
    public string characterName;               
    public Sprite characterSprite;             
    public CharacterAbilityType abilityType;   
    public CharacterAbilityType secondaryAbilityType;
}
