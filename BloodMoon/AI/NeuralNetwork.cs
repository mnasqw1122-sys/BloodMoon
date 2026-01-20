using System;
using System.IO;
using UnityEngine;
using Random = UnityEngine.Random;

namespace BloodMoon.AI
{
    [Serializable]
    public class NeuralNetwork
    {
        public int[] Layers; // 层大小：[输入, 隐藏1, ..., 输出]
        public float[][][] Weights; // [层][神经元][输入]
        public float[][] Biases; // [层][神经元]
        public float[][] Neurons; // [层][神经元] -> 输出值

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
                
                if (i > 0) // 输入层没有权重/偏置
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
                 // 如果构造函数正确调用，这不应该发生，但安全第一
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
            
            for (int i = 1; i < Biases.Length; i++) // 跳过输入层偏置
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
            // 安全地设置输入层，确保不会越界
            int inputCount = Math.Min(inputs.Length, Neurons[0].Length);
            for (int i = 0; i < inputCount; i++)
            {
                Neurons[0][i] = inputs[i];
            }
            // 对于超出输入层大小的输入，忽略它们

            // 信息传播
            for (int i = 0; i < Layers.Length - 1; i++)
            {
                int currentLayerIdx = i;
                int nextLayerIdx = i + 1;

                // 确保权重和偏置数组已正确初始化
                if (Weights == null || Weights[nextLayerIdx] == null || Biases == null || Biases[nextLayerIdx] == null)
                {
                    continue;
                }

                // 对于下一层中的每个神经元
                for (int nextNode = 0; nextNode < Layers[nextLayerIdx]; nextNode++)
                {
                    float value = 0f;

                    // 确保当前神经元的权重数组已初始化
                    if (Weights[nextLayerIdx][nextNode] == null)
                    {
                        continue;
                    }

                    // 对当前层的输入进行加权求和
                    for (int currentNode = 0; currentNode < Layers[currentLayerIdx]; currentNode++)
                    {
                        // 确保权重索引有效
                        if (currentNode < Weights[nextLayerIdx][nextNode].Length)
                        {
                            value += Neurons[currentLayerIdx][currentNode] * Weights[nextLayerIdx][nextNode][currentNode];
                        }
                    }

                    // 添加偏见
                    if (nextNode < Biases[nextLayerIdx].Length)
                    {
                        value += Biases[nextLayerIdx][nextNode];
                    }

                    // 激活
                    if (nextNode < Neurons[nextLayerIdx].Length)
                    {
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
