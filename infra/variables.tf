variable "aws_region" {
  type        = string
  default     = "us-east-1"
  description = "Região AWS."
}

variable "project" {
  type        = string
  default     = "oficina"
  description = "Prefixo de nomeação dos recursos."
}

variable "environment" {
  type        = string
  description = "Ambiente (homolog | prod)."

  validation {
    condition     = contains(["homolog", "prod"], var.environment)
    error_message = "environment deve ser homolog ou prod."
  }
}

variable "state_bucket" {
  type        = string
  description = "Bucket S3 com os estados remotos (o do infra-k8s é lido daqui)."
}

variable "namespace" {
  type        = string
  default     = "oficina-execution"
  description = "Namespace Kubernetes do serviço (o trust da role IRSA aceita só a ServiceAccount dele)."
}

variable "service_account" {
  type        = string
  default     = "execution-service"
  description = "Nome da ServiceAccount do pod, anotada com o ARN da role IRSA."
}
