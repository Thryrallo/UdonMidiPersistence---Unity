
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class CrossWorldText : UdonSharpBehaviour
{
    public string StringSaveID = "TestingCrossWorldIdentifier";
    public UdonMidiPersistence Persistence;
    public UnityEngine.UI.InputField TextField;

    [UdonSynced, FieldChangeCallback(nameof(SyncedString))]
    string _syncedString = "";

    [HideInInspector]
    public string SavedString;

    [UdonSynced]
    bool _wasEdited = false;


    // ==== Loading String ====
    void Start()
    {
        if(!_wasEdited)
        {
            Persistence.Request(this.gameObject, nameof(SavedString), nameof(LoadedText), StringSaveID);
        }
    }

    public void LoadedText()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        SyncedString = SavedString;
        RequestSerialization();
    }

    // ==== Syncing String, Save on Change ====

    public string SyncedString
    {
        get{
            return _syncedString;
        }
        set{
            _syncedString = value;
            TextField.text = value;
            SavedString = value;
            Persistence.Save(this.gameObject, nameof(SavedString), StringSaveID);
        }
    }

    // ==== UI Callbacks ====

    public void TextChanged()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        _wasEdited = true;
        SyncedString = TextField.text;
        RequestSerialization();
    }
}
