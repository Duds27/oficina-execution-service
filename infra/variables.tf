variable "aws_region" {
  type        = string
  default     = "us-east-2"
  description = "Região AWS. A conta do grupo só libera a us-east-2 (SCP da organização)."
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
  description = "Namespace Kubernetes do serviço (a associação do Pod Identity entrega a role só à ServiceAccount dele)."
}

variable "service_account" {
  type        = string
  default     = "execution-service"
  description = "Nome da ServiceAccount do pod, associada à role pelo Pod Identity."
}
