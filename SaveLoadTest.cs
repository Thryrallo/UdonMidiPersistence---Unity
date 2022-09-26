
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class SaveLoadTest : UdonSharpBehaviour
{
    public UdonMidiPersistence Persistence;
    public UnityEngine.UI.InputField TextInput;
    public UnityEngine.UI.Slider FloatInput;
    public UnityEngine.UI.Slider IntInput;

    public string StringToSave = "Hello World";
    public float FloatToSave = 0.5f;
    public int IntToSave = 5;

    public void Save()
    {
        UpdateVariablesFromUI();
        Persistence.Save(this.gameObject, nameof(StringToSave));
        Persistence.Save(this.gameObject, nameof(FloatToSave));
        Persistence.Save(this.gameObject, nameof(IntToSave));
    }

    public void Load()
    {
        Persistence.Request(this.gameObject, nameof(StringToSave), nameof(UpdateUIFromVariables));
        Persistence.Request(this.gameObject, nameof(FloatToSave), nameof(UpdateUIFromVariables));
        Persistence.Request(this.gameObject, nameof(IntToSave), nameof(UpdateUIFromVariables));
    }

    public void UpdateUIFromVariables()
    {
        TextInput.text = StringToSave;
        FloatInput.SetValueWithoutNotify(FloatToSave);
        IntInput.SetValueWithoutNotify(IntToSave);
    }

    public void UpdateVariablesFromUI()
    {
        StringToSave = TextInput.text;
        FloatToSave = FloatInput.value;
        IntToSave = (int)IntInput.value;
    }
}
