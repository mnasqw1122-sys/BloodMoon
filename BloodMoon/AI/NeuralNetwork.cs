using System;
using System.IO;
using UnityEngine;
using Random = UnityEngine.Random;

namespace BloodMoon.AI
{
    [Serializable]
    public class NeuralNetwork
    {
        public int[] Layers; // Layer sizes: [Inputs, Hidden1, ..., Outputs]
        public float[][][] Weights; // [layer][neuron][input]
        public float[][] Biases; // [layer][neuron]
        public float[][] Neurons; // [layer][neuron] -> output value

        public NeuralNetwork(int[] layers)
        {
            Layers = new int[layers.Length];
            Array.Copy(layers, Layers, layers.Length);
            
            Neurons = new float[layers.Length][];
            Weights = new float[layers.Length][][];
            Biases = new float[layers.Length][];

            for (int i = 0; i < layers.Length; i++)
            {
                Neurons[i] = new float[layers[i]];
                
                if (i > 0) // Input layer has no weights/biases
                {
                    int prevLayerSize = layers[i - 1];
                    Weights[i] = new float[layers[i]][];
                    Biases[i] = new float[layers[i]];
                    
                    for (int j = 0; j < layers[i]; j++)
                    {
                        Weights[i][j] = new float[prevLayerSize];
                    }
                }
            }
        }

        public void InitializeRandom()
        {
            if (Weights == null || Biases == null)
            {
                 // Should not happen if constructor called correctly, but safety first
                 return;
            }

            for (int i = 0; i < Weights.Length; i++)
            {
                if (Weights[i] == null) continue;
                for (int j = 0; j < Weights[i].Length; j++)
                {
                    if (Weights[i][j] == null) continue;
                    for (int k = 0; k < Weights[i][j].Length; k++)
                    {
                        Weights[i][j][k] = Random.Range(-0.5f, 0.5f);
                    }
                }
            }
            
            for (int i = 1; i < Biases.Length; i++) // Skip input layer biases
            {
                if (Biases[i] == null) continue;
                for (int j = 0; j < Biases[i].Length; j++)
                {
                    Biases[i][j] = Random.Range(-0.1f, 0.1f);
                }
            }
        }

        public float[] FeedForward(float[] inputs)
        {
            // Set input layer
            for (int i = 0; i < inputs.Length; i++)
            {
                Neurons[0][i] = inputs[i];
            }

            // Propagate
            for (int i = 0; i < Layers.Length - 1; i++)
            {
                int currentLayerIdx = i;
                int nextLayerIdx = i + 1;

                // For each neuron in next layer
                for (int nextNode = 0; nextNode < Layers[nextLayerIdx]; nextNode++)
                {
                    float value = 0f;

                    // Sum weighted inputs from current layer
                    // Weights[nextLayerIdx][nextNode] contains weights for inputs from currentLayerIdx
                    for (int currentNode = 0; currentNode < Layers[currentLayerIdx]; currentNode++)
                    {
                        value += Neurons[currentLayerIdx][currentNode] * Weights[nextLayerIdx][nextNode][currentNode];
                    }

                    // Add bias
                    value += Biases[nextLayerIdx][nextNode];

                    // Activation
                    if (nextLayerIdx == Layers.Length - 1)
                    {
                        Neurons[nextLayerIdx][nextNode] = Sigmoid(value);
                    }
                    else
                    {
                        Neurons[nextLayerIdx][nextNode] = (float)Math.Tanh(value);
                    }
                }
            }

            return Neurons[Layers.Length - 1];
        }

        private float Sigmoid(float value)
        {
            return 1.0f / (1.0f + (float)Math.Exp(-value));
        }

        public void Mutate(float mutationRate, float mutationStrength)
        {
            for (int i = 0; i < Weights.Length; i++)
            {
                for (int j = 0; j < Weights[i].Length; j++)
                {
                    for (int k = 0; k < Weights[i][j].Length; k++)
                    {
                        if (Random.value < mutationRate)
                        {
                            Weights[i][j][k] += Random.Range(-mutationStrength, mutationStrength);
                        }
                    }
                }
            }
            
            for (int i = 1; i < Biases.Length; i++)
            {
                for (int j = 0; j < Biases[i].Length; j++)
                {
                    if (Random.value < mutationRate)
                    {
                        Biases[i][j] += Random.Range(-mutationStrength, mutationStrength);
                    }
                }
            }
        }
        
        public string SaveToString()
        {
             return JsonUtility.ToJson(this);
        }

        public static NeuralNetwork LoadFromString(string json)
        {
             return JsonUtility.FromJson<NeuralNetwork>(json);
        }
    }
}
