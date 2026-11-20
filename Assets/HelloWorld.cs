using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HelloWorld : MonoBehaviour
{
    //Свойства
    [Header("Свойства персонажа")]
    [Range(0, 1000)]
    public int life = 100;
    public int mana = 100;


    //Действия
    void Start()
    {
        print("Привет мир!");
    }



}
